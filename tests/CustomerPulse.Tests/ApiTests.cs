using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CustomerPulse;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CustomerPulse.Tests;

public sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero); // Monday
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class RecordingAlerts : IAlertSink
{
    public List<(FollowUp F, Response R)> Sent { get; } = [];
    public Task AlertAsync(FollowUp f, Response r, CancellationToken ct) { Sent.Add((f, r)); return Task.CompletedTask; }
}

public sealed class PulseFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"pulse-test-{Guid.NewGuid():N}.db");
    public FixedClock Clock { get; } = new();
    public RecordingAlerts Alerts { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Pulse", $"Data Source={_db}");
        builder.UseSetting("Pulse:BaseUrl", "https://pulse.example.co.za/");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
            s.RemoveAll<IAlertSink>();
            s.AddSingleton<IAlertSink>(Alerts);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        if (File.Exists(_db)) File.Delete(_db);
    }
}

public class ApiTests : IDisposable
{
    private readonly PulseFactory _f = new();
    private readonly HttpClient _c;
    public ApiTests() => _c = _f.CreateClient();
    public void Dispose() => _f.Dispose();

    private async Task<int> NewSurvey()
    {
        var res = await _c.PostAsJsonAsync("/api/surveys", new { name = "October deliveries" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<List<string>> Invite(int survey, string csv)
    {
        var res = await _c.PostAsync($"/api/surveys/{survey}/invites", new StringContent(csv, Encoding.UTF8, "text/csv"));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(e => e.GetProperty("link").GetString()!).ToList();
    }

    private async Task<string> Answer(string link, int score, string comment)
    {
        var path = new Uri(link).AbsolutePath;
        var res = await _c.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["score"] = score.ToString(), ["comment"] = comment }));
        return await res.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Invite_answer_once_and_follow_up_detractors()
    {
        var id = await NewSurvey();
        var links = await Invite(id, "name,contact,segment\nThandi,thandi@example.co.za,Gold\nSipho,sipho@example.co.za,Standard\nSIPHO,SIPHO@example.co.za,Standard\nNaledi,naledi@example.co.za,Gold");
        Assert.Equal(3, links.Count); // duplicate contact skipped
        Assert.Matches(new Regex("^https://pulse.example.co.za/r/[0-9a-f]{32}$"), links[0]);

        var page = await _c.GetStringAsync(new Uri(links[0]).AbsolutePath);
        Assert.Contains("Hi Thandi", page);
        Assert.Contains("How likely are you to recommend us", page);

        Assert.Contains("Thank you!", await Answer(links[0], 10, "Super fast delivery and friendly driver"));
        Assert.Contains("already answered", await Answer(links[0], 0, "changed my mind"));
        await Answer(links[1], 3, "Parcel arrived damaged and nobody answered my complaint");
        await Answer(links[2], 8, "");

        var alert = Assert.Single(_f.Alerts.Sent);
        Assert.Equal("Detractor + Negative comment", alert.F.Reason);
        Assert.Equal("Sipho", alert.F.CustomerName);

        var open = await _c.GetFromJsonAsync<JsonElement>("/api/followups?status=Open");
        var fid = open.EnumerateArray().Single().GetProperty("id").GetInt32();
        (await _c.PostAsJsonAsync($"/api/followups/{fid}/close", new { note = "Called, replacement sent", closedBy = "Ohula" })).EnsureSuccessStatusCode();
        Assert.Empty((await _c.GetFromJsonAsync<JsonElement>("/api/followups?status=Open")).EnumerateArray());

        var s = await _c.GetFromJsonAsync<JsonElement>($"/api/surveys/{id}/summary");
        Assert.Equal(3, s.GetProperty("responses").GetInt32());
        Assert.Equal(0, s.GetProperty("nps").GetDouble()); // 1 promoter, 1 passive, 1 detractor
        Assert.Equal(100, s.GetProperty("responseRate").GetDouble());
        Assert.Equal(50, s.GetProperty("npsBySegment").GetProperty("Gold").GetDouble());     // Thandi 10, Naledi 8
        Assert.Equal(-100, s.GetProperty("npsBySegment").GetProperty("Standard").GetDouble()); // Sipho 3
        Assert.Equal("delivery", s.GetProperty("topics")[0].GetProperty("topic").GetString());
        Assert.Equal(0, s.GetProperty("openFollowUps").GetInt32());
    }

    [Fact]
    public async Task Anonymous_responses_weekly_trend_and_csv()
    {
        var id = await NewSurvey();
        async Task Anon(int score, string comment) =>
            (await _c.PostAsJsonAsync($"/api/surveys/{id}/responses", new { score, comment, segment = "Store: Sandton" })).EnsureSuccessStatusCode();

        await Anon(9, "Great service");
        await Anon(4, "Long queue, slow tills");
        _f.Clock.Now = _f.Clock.Now.AddDays(7);
        await Anon(10, "Loved it");
        await Anon(10, "");

        var bad = await _c.PostAsJsonAsync($"/api/surveys/{id}/responses", new { score = 11 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var s = await _c.GetFromJsonAsync<JsonElement>($"/api/surveys/{id}/summary");
        var weeks = s.GetProperty("weekly").EnumerateArray().Select(w => (w.GetProperty("weekStarting").GetString(), w.GetProperty("nps").GetDouble())).ToList();
        Assert.Equal([("2026-10-12", 0.0), ("2026-10-19", 100.0)], weeks);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("responseRate").ValueKind);

        var lastWeek = await _c.GetFromJsonAsync<JsonElement>($"/api/surveys/{id}/summary?from=2026-10-19T00:00:00%2B02:00");
        Assert.Equal(2, lastWeek.GetProperty("responses").GetInt32());

        var negative = await _c.GetFromJsonAsync<JsonElement>($"/api/surveys/{id}/responses?topic=wait%20time");
        Assert.Equal("Long queue, slow tills", negative.EnumerateArray().Single().GetProperty("comment").GetString());

        var csv = await _c.GetStringAsync($"/api/surveys/{id}/responses.csv");
        Assert.StartsWith("id,created_at,score,group", csv);
        Assert.Contains(",4,Detractor,Store: Sandton,", csv);
    }

    [Fact]
    public async Task Closed_surveys_and_unknown_links()
    {
        var id = await NewSurvey();
        var link = (await Invite(id, "Thandi,thandi@example.co.za"))[0];
        (await _c.PostAsync($"/api/surveys/{id}/close", null)).EnsureSuccessStatusCode();
        Assert.Contains("no longer collecting", await _c.GetStringAsync(new Uri(link).AbsolutePath));
        Assert.Contains("closed", await Answer(link, 9, "late answer"));
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync("/r/0000")).StatusCode);
    }

    [Fact]
    public async Task Customer_names_are_html_encoded()
    {
        var id = await NewSurvey();
        var link = (await Invite(id, "<script>alert(1)</script>,x@example.co.za"))[0];
        var page = await _c.GetStringAsync(new Uri(link).AbsolutePath);
        Assert.DoesNotContain("<script>alert", page);
        Assert.Contains("&lt;script&gt;", page);
    }
}
