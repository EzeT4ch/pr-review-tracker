namespace Prt.Core.Domain;

public static class MemoKinds
{
    public const string AgentSummary = "agent_summary";
    public const string UserNote = "user_note";
    public const string Decision = "decision";

    public static readonly string[] All = [AgentSummary, UserNote, Decision];

    public static string Label(string kind) => kind switch
    {
        AgentSummary => "Resumen del agente",
        UserNote => "Nota",
        Decision => "Decisión",
        _ => kind,
    };
}

public static class RoundKinds
{
    public const string New = "new";
    public const string Resume = "resume";
}

public sealed class PullRequest
{
    public long Id { get; set; }
    public required string Workspace { get; set; }
    public required string Repo { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string SourceBranch { get; set; } = "";
    public string DestinationBranch { get; set; } = "";
    public string State { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Head commit seen the last time the PR was reviewed.</summary>
    public string? LastReviewedCommit { get; set; }
    public string? Verdict { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public PrRef Ref => new(Workspace, Repo, Number);
    public string Key => Ref.Key;
}

public sealed class ReviewSession
{
    public long Id { get; set; }
    public long PrId { get; set; }
    public required string Agent { get; set; }
    public required string AgentSessionId { get; set; }
    public string Cwd { get; set; } = "";
    public string? Label { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}

public sealed class Round
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? CommitAtStart { get; set; }
    public string? Prompt { get; set; }
}

public sealed class Memo
{
    public long Id { get; set; }
    public long PrId { get; set; }
    public long? RoundId { get; set; }
    public required string Kind { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Everything known about one PR, as needed for display and export.</summary>
public sealed record PrHistory(
    PullRequest Pr,
    IReadOnlyList<ReviewSession> Sessions,
    IReadOnlyList<Round> Rounds,
    IReadOnlyList<Memo> Memos,
    IReadOnlyList<string> Tags);
