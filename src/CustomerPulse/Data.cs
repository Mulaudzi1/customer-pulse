using Microsoft.EntityFrameworkCore;

namespace CustomerPulse;

public sealed class Survey
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Question { get; set; } = "How likely are you to recommend us to a friend or colleague?";
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Invite
{
    public int Id { get; set; }
    public int SurveyId { get; set; }
    /// <summary>Unguessable link token, so one customer can't answer for another or answer twice.</summary>
    public string Token { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string Contact { get; set; } = "";
    public string? Segment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}

public sealed class Response
{
    public int Id { get; set; }
    public int SurveyId { get; set; }
    public int? InviteId { get; set; }
    public int Score { get; set; }
    public string? Comment { get; set; }
    public string? Segment { get; set; }
    public double Sentiment { get; set; }
    public string SentimentLabel { get; set; } = "none";
    /// <summary>Comma-separated topic tags.</summary>
    public string Topics { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public enum FollowUpStatus { Open, Closed }

public sealed class FollowUp
{
    public int Id { get; set; }
    public int ResponseId { get; set; }
    public Response? Response { get; set; }
    public string Reason { get; set; } = "";
    public string? CustomerName { get; set; }
    public string? Contact { get; set; }
    public FollowUpStatus Status { get; set; }
    public string? Note { get; set; }
    public string? ClosedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}

public sealed class PulseDb(DbContextOptions<PulseDb> options) : DbContext(options)
{
    public DbSet<Survey> Surveys => Set<Survey>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<Response> Responses => Set<Response>();
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b) =>
        b.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Invite>().HasIndex(x => x.Token).IsUnique();
        b.Entity<Response>().HasIndex(x => new { x.SurveyId, x.CreatedAt });
        b.Entity<Response>().HasIndex(x => x.InviteId).IsUnique();
    }
}

public sealed class UtcTicksConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
    v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
