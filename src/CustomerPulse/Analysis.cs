using System.Text.RegularExpressions;

namespace CustomerPulse;

public enum NpsGroup { Detractor, Passive, Promoter }

public static class Nps
{
    public static NpsGroup Group(int score) => score switch
    {
        < 0 or > 10 => throw new ArgumentOutOfRangeException(nameof(score), "NPS scores run from 0 to 10."),
        <= 6 => NpsGroup.Detractor,
        <= 8 => NpsGroup.Passive,
        _ => NpsGroup.Promoter,
    };

    /// <summary>Net Promoter Score: % promoters minus % detractors, from -100 to 100. Null when there are no responses.</summary>
    public static double? Score(IEnumerable<int> scores)
    {
        var list = scores.ToList();
        if (list.Count == 0) return null;
        var promoters = list.Count(s => s >= 9);
        var detractors = list.Count(s => s <= 6);
        return Math.Round(100.0 * (promoters - detractors) / list.Count, 1);
    }

    /// <summary>
    /// Margin of error (95%) for an NPS, so a dashboard doesn't celebrate a jump from 12 responses.
    /// Treats each answer as +1/0/-1 and uses the standard error of the mean.
    /// </summary>
    public static double? MarginOfError(IEnumerable<int> scores)
    {
        var values = scores.Select(s => s >= 9 ? 1.0 : s <= 6 ? -1.0 : 0.0).ToList();
        if (values.Count < 2) return null;
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return Math.Round(100 * 1.96 * Math.Sqrt(variance / values.Count), 1);
    }
}

public sealed record TextInsights(double Sentiment, string Label, IReadOnlyList<string> Topics);

/// <summary>
/// Lexicon-based sentiment and keyword topic tagging. It runs offline with no model download, and the
/// word lists are easy for a business to read and tune. Handles simple negation ("not good") and intensifiers.
/// </summary>
public static class TextAnalyzer
{
    private static readonly Dictionary<string, double> Lexicon = new(StringComparer.OrdinalIgnoreCase)
    {
        // positive
        ["good"] = 1, ["great"] = 2, ["excellent"] = 2.5, ["amazing"] = 2.5, ["awesome"] = 2.5, ["love"] = 2, ["loved"] = 2,
        ["friendly"] = 1.5, ["helpful"] = 1.5, ["fast"] = 1, ["quick"] = 1, ["easy"] = 1, ["happy"] = 1.5, ["recommend"] = 1.5,
        ["professional"] = 1.5, ["clean"] = 1, ["fresh"] = 1, ["perfect"] = 2.5, ["best"] = 2, ["nice"] = 1, ["polite"] = 1.5,
        ["affordable"] = 1, ["thanks"] = 1, ["thank"] = 1, ["lekker"] = 2, ["sharp"] = 1, ["efficient"] = 1.5, ["reliable"] = 1.5,
        // negative
        ["bad"] = -1.5, ["terrible"] = -2.5, ["awful"] = -2.5, ["horrible"] = -2.5, ["worst"] = -3, ["rude"] = -2, ["slow"] = -1.5,
        ["late"] = -1.5, ["cold"] = -1, ["dirty"] = -2, ["expensive"] = -1, ["overpriced"] = -1.5, ["broken"] = -2, ["wrong"] = -1.5,
        ["never"] = -1, ["disappointed"] = -2, ["disappointing"] = -2, ["poor"] = -1.5, ["waited"] = -1, ["waiting"] = -1,
        ["unhelpful"] = -1.5, ["ignored"] = -2, ["refund"] = -1, ["complaint"] = -1.5, ["missing"] = -1.5, ["damaged"] = -2, ["kak"] = -2,
    };

    private static readonly HashSet<string> Negators = new(StringComparer.OrdinalIgnoreCase) { "not", "no", "never", "isn't", "wasn't", "didn't", "don't", "hardly", "nothing" };
    private static readonly Dictionary<string, double> Intensifiers = new(StringComparer.OrdinalIgnoreCase) { ["very"] = 1.5, ["really"] = 1.5, ["so"] = 1.3, ["extremely"] = 2, ["super"] = 1.5 };

    public static readonly IReadOnlyDictionary<string, string[]> TopicKeywords = new Dictionary<string, string[]>
    {
        ["delivery"] = ["delivery", "deliver", "delivered", "courier", "driver", "arrived", "late", "shipping"],
        ["price"] = ["price", "prices", "expensive", "cheap", "affordable", "overpriced", "cost", "value"],
        ["staff"] = ["staff", "rude", "friendly", "helpful", "polite", "waiter", "cashier", "agent", "consultant", "service"],
        ["wait time"] = ["wait", "waited", "waiting", "queue", "slow", "quick", "fast", "hours"],
        ["quality"] = ["quality", "broken", "damaged", "fresh", "cold", "taste", "defective", "works"],
        ["app & website"] = ["app", "website", "site", "online", "login", "checkout", "crash", "crashed"],
        ["billing"] = ["bill", "billing", "invoice", "charged", "refund", "payment", "debit"],
    };

    private static readonly Regex Word = new(@"[\p{L}']+", RegexOptions.Compiled);
    // Negation only reaches to the end of its clause: "never late, always lekker" keeps "lekker" positive.
    private static readonly Regex ClauseBreak = new(@"[,.;:!?]|\bbut\b", RegexOptions.Compiled);

    public static TextInsights Analyze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new TextInsights(0, "none", []);
        var lower = text.ToLowerInvariant();
        var words = Word.Matches(lower).Select(m => m.Value).ToList();
        double total = 0;
        foreach (var clause in ClauseBreak.Split(lower))
            total += ScoreClause(Word.Matches(clause).Select(m => m.Value).ToList());
        // Squash into -1..1 so long and short comments are comparable.
        var score = Math.Round(total / Math.Sqrt(total * total + 15), 3);
        var label = score >= 0.15 ? "positive" : score <= -0.15 ? "negative" : "neutral";
        var topics = TopicKeywords.Where(t => t.Value.Any(k => words.Contains(k))).Select(t => t.Key).ToList();
        return new TextInsights(score, label, topics);
    }

    private static double ScoreClause(List<string> words)
    {
        double total = 0;
        for (var i = 0; i < words.Count; i++)
        {
            if (!Lexicon.TryGetValue(words[i], out var value)) continue;
            if (Negators.Contains(words[i]) && i + 1 < words.Count && Lexicon.ContainsKey(words[i + 1])) continue; // "never late": let the negation flip "late"
            for (var back = 1; back <= 3 && i - back >= 0; back++)
            {
                if (Negators.Contains(words[i - back])) { value *= -0.75; break; }
            }
            if (i > 0 && Intensifiers.TryGetValue(words[i - 1], out var boost)) value *= boost;
            total += value;
        }
        return total;
    }
}
