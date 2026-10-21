using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CustomerPulse;

public sealed class PulseOptions
{
    /// <summary>Public base URL used to build survey links.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";
    /// <summary>Optional Slack incoming-webhook URL for detractor alerts. Keep it in an environment variable.</summary>
    public string? SlackWebhookUrl { get; set; }
}

public interface IAlertSink
{
    Task AlertAsync(FollowUp followUp, Response response, CancellationToken ct);
}

public sealed class AlertSink(IHttpClientFactory http, IOptions<PulseOptions> options, ILogger<AlertSink> log) : IAlertSink
{
    public async Task AlertAsync(FollowUp f, Response r, CancellationToken ct)
    {
        var text = $"Customer Pulse: {f.Reason} — score {r.Score}/10 from {f.CustomerName ?? "anonymous"}" +
                   (string.IsNullOrWhiteSpace(r.Comment) ? "" : $": \"{r.Comment}\"");
        log.LogWarning("{Alert}", text);
        if (string.IsNullOrEmpty(options.Value.SlackWebhookUrl)) return;
        try
        {
            using var res = await http.CreateClient("slack").PostAsJsonAsync(options.Value.SlackWebhookUrl, new { text }, ct);
            if (!res.IsSuccessStatusCode) log.LogError("Slack alert failed: {Status}", res.StatusCode);
        }
        catch (HttpRequestException ex) { log.LogError(ex, "Slack alert failed"); }
    }
}

public sealed record NewInvite(string Name, string Contact, string? Segment);

public sealed class SurveyService(PulseDb db, IAlertSink alerts, TimeProvider clock)
{
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public async Task<List<Invite>> CreateInvitesAsync(int surveyId, IEnumerable<NewInvite> people, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var invites = people
            .Where(p => !string.IsNullOrWhiteSpace(p.Contact))
            .DistinctBy(p => p.Contact.Trim().ToLowerInvariant())
            .Select(p => new Invite
            {
                SurveyId = surveyId, Token = NewToken(), CustomerName = p.Name.Trim(), Contact = p.Contact.Trim(),
                Segment = string.IsNullOrWhiteSpace(p.Segment) ? null : p.Segment.Trim(), CreatedAt = now,
            }).ToList();
        db.Invites.AddRange(invites);
        await db.SaveChangesAsync(ct);
        return invites;
    }

    /// <summary>Parses "name,contact,segment" lines (header optional, quotes allowed).</summary>
    public static IEnumerable<NewInvite> ParseCsv(string csv)
    {
        foreach (var raw in csv.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var cells = SplitCsvLine(line);
            if (cells.Count < 2 || cells[0].Equals("name", StringComparison.OrdinalIgnoreCase)) continue;
            yield return new NewInvite(cells[0], cells[1], cells.Count > 2 ? cells[2] : null);
        }
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { cells.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(c);
        }
        cells.Add(cur.ToString().Trim());
        return cells;
    }

    public sealed record SubmitResult(bool Ok, string? Error, Response? Response = null);

    public async Task<SubmitResult> SubmitAsync(int surveyId, Invite? invite, int score, string? comment, string? segment, CancellationToken ct)
    {
        if (score is < 0 or > 10) return new(false, "Score must be between 0 and 10.");
        var survey = await db.Surveys.FindAsync([surveyId], ct);
        if (survey is null || !survey.Active) return new(false, "This survey is closed.");
        if (invite?.RespondedAt is not null) return new(false, "You've already answered this survey. Thank you!");
        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 2000)];

        var now = clock.GetUtcNow();
        var insights = TextAnalyzer.Analyze(comment);
        var response = new Response
        {
            SurveyId = surveyId, InviteId = invite?.Id, Score = score, Comment = comment, Segment = invite?.Segment ?? segment,
            Sentiment = insights.Sentiment, SentimentLabel = insights.Label, Topics = string.Join(",", insights.Topics), CreatedAt = now,
        };
        db.Responses.Add(response);
        if (invite is not null) invite.RespondedAt = now;
        await db.SaveChangesAsync(ct);

        var reasons = new List<string>();
        if (Nps.Group(score) == NpsGroup.Detractor) reasons.Add("Detractor");
        if (insights.Label == "negative") reasons.Add("Negative comment");
        if (reasons.Count > 0)
        {
            var followUp = new FollowUp
            {
                ResponseId = response.Id, Reason = string.Join(" + ", reasons), CustomerName = invite?.CustomerName,
                Contact = invite?.Contact, CreatedAt = now,
            };
            db.FollowUps.Add(followUp);
            await db.SaveChangesAsync(ct);
            await alerts.AlertAsync(followUp, response, ct);
        }
        return new(true, null, response);
    }
}

public sealed record TopicStat(string Topic, int Mentions, double AverageScore, double AverageSentiment);
public sealed record WeekStat(string WeekStarting, int Responses, double? Nps);
public sealed record Summary(int Responses, int Promoters, int Passives, int Detractors, double? Nps, double? MarginOfError,
    double? ResponseRate, double AverageSentiment, IReadOnlyList<TopicStat> Topics, IReadOnlyList<WeekStat> Weekly,
    IReadOnlyDictionary<string, double?> NpsBySegment, int OpenFollowUps);

public static class Reports
{
    public static async Task<Summary> SummarizeAsync(PulseDb db, int surveyId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var q = db.Responses.Where(r => r.SurveyId == surveyId);
        if (from is { } f) q = q.Where(r => r.CreatedAt >= f);
        if (to is { } t) q = q.Where(r => r.CreatedAt < t);
        var rows = await q.ToListAsync(ct);
        var invites = await db.Invites.CountAsync(i => i.SurveyId == surveyId, ct);
        var answered = rows.Count(r => r.InviteId != null);
        var openFollowUps = await db.FollowUps.CountAsync(x => x.Status == FollowUpStatus.Open && x.Response!.SurveyId == surveyId, ct);
        var scores = rows.Select(r => r.Score).ToList();

        var topics = rows
            .SelectMany(r => r.Topics.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => (Topic: t, r)))
            .GroupBy(x => x.Topic)
            .Select(g => new TopicStat(g.Key, g.Count(), Math.Round(g.Average(x => x.r.Score), 1), Math.Round(g.Average(x => x.r.Sentiment), 2)))
            .OrderByDescending(t => t.Mentions).ThenBy(t => t.AverageScore).ToList();

        var weekly = rows
            .GroupBy(r => WeekStart(r.CreatedAt))
            .OrderBy(g => g.Key)
            .Select(g => new WeekStat(g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g.Count(), Nps.Score(g.Select(r => r.Score))))
            .ToList();

        var bySegment = rows.Where(r => r.Segment != null).GroupBy(r => r.Segment!)
            .ToDictionary(g => g.Key, g => Nps.Score(g.Select(r => r.Score)));

        return new Summary(rows.Count, scores.Count(s => s >= 9), scores.Count(s => s is 7 or 8), scores.Count(s => s <= 6),
            Nps.Score(scores), Nps.MarginOfError(scores), invites == 0 ? null : Math.Round(100.0 * answered / invites, 1),
            rows.Count == 0 ? 0 : Math.Round(rows.Average(r => r.Sentiment), 2), topics, weekly, bySegment, openFollowUps);
    }

    /// <summary>Monday of the response's week, in South African time.</summary>
    public static DateOnly WeekStart(DateTimeOffset t)
    {
        var local = DateOnly.FromDateTime(t.ToOffset(TimeSpan.FromHours(2)).DateTime);
        return local.AddDays(-(((int)local.DayOfWeek + 6) % 7));
    }

    public static string ToCsv(IEnumerable<Response> rows)
    {
        static string Esc(string? s) => s is null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 || s.StartsWith('=') || s.StartsWith('+') || s.StartsWith('-') || s.StartsWith('@')
            ? "\"" + (s.StartsWith('=') || s.StartsWith('+') || s.StartsWith('-') || s.StartsWith('@') ? "'" : "") + s.Replace("\"", "\"\"") + "\""
            : s;
        var sb = new System.Text.StringBuilder("id,created_at,score,group,segment,sentiment,sentiment_label,topics,comment\n");
        foreach (var r in rows)
            sb.Append(CultureInfo.InvariantCulture, $"{r.Id},{r.CreatedAt:O},{r.Score},{Nps.Group(r.Score)},{Esc(r.Segment)},{r.Sentiment},{r.SentimentLabel},{Esc(r.Topics)},{Esc(r.Comment)}\n");
        return sb.ToString();
    }
}
