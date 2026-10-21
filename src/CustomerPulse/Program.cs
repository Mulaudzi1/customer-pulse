using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using CustomerPulse;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<PulseOptions>(builder.Configuration.GetSection("Pulse"));
builder.Services.AddDbContext<PulseDb>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Pulse") ?? "Data Source=pulse.db"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("slack");
builder.Services.AddScoped<IAlertSink, AlertSink>();
builder.Services.AddScoped<SurveyService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<PulseDb>().Database.EnsureCreated();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --- Customer-facing survey page (the link in the email or SMS) ---

app.MapGet("/r/{token}", async (string token, PulseDb db) =>
{
    var invite = await db.Invites.SingleOrDefaultAsync(i => i.Token == token);
    if (invite is null) return Results.NotFound();
    var survey = (await db.Surveys.FindAsync(invite.SurveyId))!;
    if (invite.RespondedAt is not null) return SurveyPage.Message("Thank you!", "You've already answered this survey.");
    if (!survey.Active) return SurveyPage.Message("Survey closed", "This survey is no longer collecting answers.");
    return SurveyPage.Form(survey, invite);
});

app.MapPost("/r/{token}", async (string token, [FromForm] int score, [FromForm] string? comment, PulseDb db, SurveyService surveys, CancellationToken ct) =>
{
    var invite = await db.Invites.SingleOrDefaultAsync(i => i.Token == token, ct);
    if (invite is null) return Results.NotFound();
    var result = await surveys.SubmitAsync(invite.SurveyId, invite, score, comment, null, ct);
    return result.Ok
        ? SurveyPage.Message("Thank you!", "Your feedback helps us get better.")
        : SurveyPage.Message("Sorry", result.Error!);
}).DisableAntiforgery(); // the unguessable token in the URL is the credential

// --- Staff API ---

var api = app.MapGroup("/api");

api.MapPost("/surveys", async (Survey input, PulseDb db, TimeProvider clock) =>
{
    if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Name is required." });
    var s = new Survey { Name = input.Name.Trim(), Question = string.IsNullOrWhiteSpace(input.Question) ? new Survey().Question : input.Question.Trim(), CreatedAt = clock.GetUtcNow() };
    db.Surveys.Add(s);
    await db.SaveChangesAsync();
    return Results.Created($"/api/surveys/{s.Id}", s);
});

api.MapGet("/surveys", async (PulseDb db) => await db.Surveys.OrderByDescending(s => s.Id).ToListAsync());

api.MapPost("/surveys/{id:int}/close", async (int id, PulseDb db) =>
{
    var s = await db.Surveys.FindAsync(id);
    if (s is null) return Results.NotFound();
    s.Active = false;
    await db.SaveChangesAsync();
    return Results.Ok(s);
});

// Body: JSON array of { name, contact, segment } or text/csv "name,contact,segment" lines.
api.MapPost("/surveys/{id:int}/invites", async (int id, HttpRequest req, PulseDb db, SurveyService surveys, IOptions<PulseOptions> o, CancellationToken ct) =>
{
    if (await db.Surveys.FindAsync([id], ct) is null) return Results.NotFound();
    List<NewInvite> people;
    if (req.ContentType?.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase) == true)
    {
        using var reader = new StreamReader(req.Body);
        people = SurveyService.ParseCsv(await reader.ReadToEndAsync(ct)).ToList();
    }
    else people = await req.ReadFromJsonAsync<List<NewInvite>>(ct) ?? [];
    var created = await surveys.CreateInvitesAsync(id, people, ct);
    var baseUrl = o.Value.BaseUrl.TrimEnd('/');
    return Results.Ok(created.Select(i => new { i.Id, i.CustomerName, i.Contact, i.Segment, link = $"{baseUrl}/r/{i.Token}" }));
});

// Anonymous responses, e.g. from a QR code at the till or a website widget.
api.MapPost("/surveys/{id:int}/responses", async (int id, AnonymousResponse body, SurveyService surveys, CancellationToken ct) =>
{
    var result = await surveys.SubmitAsync(id, null, body.Score, body.Comment, body.Segment, ct);
    return result.Ok ? Results.Ok(result.Response) : Results.BadRequest(new { error = result.Error });
});

api.MapGet("/surveys/{id:int}/summary", async (int id, DateTimeOffset? from, DateTimeOffset? to, PulseDb db, CancellationToken ct) =>
    await db.Surveys.FindAsync([id], ct) is null ? Results.NotFound() : Results.Ok(await Reports.SummarizeAsync(db, id, from, to, ct)));

api.MapGet("/surveys/{id:int}/responses", async (int id, PulseDb db, string? sentiment, string? topic) =>
{
    var q = db.Responses.Where(r => r.SurveyId == id);
    if (sentiment is not null) q = q.Where(r => r.SentimentLabel == sentiment);
    if (topic is not null) q = q.Where(r => ("," + r.Topics + ",").Contains("," + topic + ","));
    return await q.OrderByDescending(r => r.Id).Take(500).ToListAsync();
});

api.MapGet("/surveys/{id:int}/responses.csv", async (int id, PulseDb db) =>
    Results.Text(Reports.ToCsv(await db.Responses.Where(r => r.SurveyId == id).OrderBy(r => r.Id).ToListAsync()), "text/csv"));

api.MapGet("/followups", async (PulseDb db, FollowUpStatus? status) =>
    await db.FollowUps.Include(f => f.Response).Where(f => status == null || f.Status == status).OrderByDescending(f => f.Id).ToListAsync());

api.MapPost("/followups/{id:int}/close", async (int id, CloseFollowUp body, PulseDb db, TimeProvider clock) =>
{
    var f = await db.FollowUps.FindAsync(id);
    if (f is null) return Results.NotFound();
    f.Status = FollowUpStatus.Closed;
    f.Note = body.Note;
    f.ClosedBy = body.ClosedBy;
    f.ClosedAt = clock.GetUtcNow();
    await db.SaveChangesAsync();
    return Results.Ok(f);
});

app.Run();

public sealed record AnonymousResponse(int Score, string? Comment, string? Segment);
public sealed record CloseFollowUp(string? Note, string? ClosedBy);

static class SurveyPage
{
    private static readonly HtmlEncoder H = HtmlEncoder.Default;
    private const string Style = "<style>body{font-family:system-ui,sans-serif;max-width:560px;margin:40px auto;padding:0 16px;color:#111}" +
        ".scale{display:flex;gap:4px;flex-wrap:wrap}.scale label{flex:1;min-width:38px;text-align:center;border:1px solid #ccc;border-radius:6px;padding:8px 0;cursor:pointer}" +
        ".scale input{display:none}.scale input:checked+span{font-weight:700;color:#0a7}textarea{width:100%;min-height:90px;margin-top:12px}" +
        "button{margin-top:12px;padding:10px 18px;border:0;border-radius:6px;background:#0a7;color:#fff;font-size:16px}.ends{display:flex;justify-content:space-between;color:#666;font-size:13px}</style>";

    public static IResult Form(Survey s, Invite i)
    {
        var scale = string.Concat(Enumerable.Range(0, 11).Select(n => $"<label><input type=radio name=score value={n} required><span>{n}</span></label>"));
        var greeting = string.IsNullOrWhiteSpace(i.CustomerName) ? "" : $"<p>Hi {H.Encode(i.CustomerName)},</p>";
        return Results.Content($"<!doctype html><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\"><title>{H.Encode(s.Name)}</title>{Style}" +
            $"{greeting}<h2>{H.Encode(s.Question)}</h2><form method=post><div class=scale>{scale}</div><div class=ends><span>Not likely</span><span>Very likely</span></div>" +
            "<textarea name=comment maxlength=2000 placeholder=\"What's the main reason for your score? (optional)\"></textarea><button>Send</button></form>", "text/html");
    }

    public static IResult Message(string title, string text) =>
        Results.Content($"<!doctype html><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\"><title>{H.Encode(title)}</title>{Style}<h2>{H.Encode(title)}</h2><p>{H.Encode(text)}</p>", "text/html");
}

public partial class Program;
