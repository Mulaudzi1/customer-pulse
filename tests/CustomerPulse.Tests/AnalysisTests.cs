using CustomerPulse;

namespace CustomerPulse.Tests;

public class NpsTests
{
    [Theory]
    [InlineData(0, NpsGroup.Detractor)]
    [InlineData(6, NpsGroup.Detractor)]
    [InlineData(7, NpsGroup.Passive)]
    [InlineData(8, NpsGroup.Passive)]
    [InlineData(9, NpsGroup.Promoter)]
    [InlineData(10, NpsGroup.Promoter)]
    public void Groups(int score, NpsGroup expected) => Assert.Equal(expected, Nps.Group(score));

    [Fact]
    public void Score_and_margin()
    {
        Assert.Null(Nps.Score([]));
        Assert.Equal(100, Nps.Score([9, 10]));
        Assert.Equal(-100, Nps.Score([0, 6]));
        Assert.Equal(20, Nps.Score([10, 10, 9, 8, 7, 6, 3, 9, 10, 2])); // 5 promoters, 3 detractors of 10
        Assert.True(Nps.MarginOfError([10, 10, 9, 8, 7, 6, 3, 9, 10, 2]) > 40); // tiny sample: wide margin
        Assert.Null(Nps.MarginOfError([10]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Nps.Group(11));
    }
}

public class TextAnalyzerTests
{
    [Theory]
    [InlineData("The staff were really friendly and the delivery was fast", "positive")]
    [InlineData("Driver was late and rude, food arrived cold", "negative")]
    [InlineData("Not good at all", "negative")]
    [InlineData("Never late, always lekker", "positive")]
    [InlineData("I ordered a shirt", "neutral")]
    [InlineData("", "none")]
    public void Sentiment_labels(string text, string label) => Assert.Equal(label, TextAnalyzer.Analyze(text).Label);

    [Fact]
    public void Intensifiers_and_negation_change_strength()
    {
        Assert.True(TextAnalyzer.Analyze("very good").Sentiment > TextAnalyzer.Analyze("good").Sentiment);
        Assert.True(TextAnalyzer.Analyze("not bad").Sentiment > TextAnalyzer.Analyze("bad").Sentiment);
    }

    [Fact]
    public void Topics_are_tagged()
    {
        var t = TextAnalyzer.Analyze("Waited two hours in the queue and was charged twice, the cashier was rude");
        Assert.Equal(["staff", "wait time", "billing"], t.Topics);
        Assert.Equal(["app & website"], TextAnalyzer.Analyze("The app crashed at checkout").Topics);
    }
}

public class CsvTests
{
    [Fact]
    public void Parses_invite_csv_with_header_and_quotes()
    {
        var rows = SurveyService.ParseCsv("name,contact,segment\n\"Dlamini, Thandi\",thandi@example.co.za,Gold\nSipho,+27821234567\n\n").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new NewInvite("Dlamini, Thandi", "thandi@example.co.za", "Gold"), rows[0]);
        Assert.Null(rows[1].Segment);
    }

    [Fact]
    public void Export_neutralises_spreadsheet_formulas()
    {
        var csv = Reports.ToCsv([new Response { Id = 1, Score = 3, Comment = "=HYPERLINK(\"x\")", Topics = "" }]);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);
    }
}
