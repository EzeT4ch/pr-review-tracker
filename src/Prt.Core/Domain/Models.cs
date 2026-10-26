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

    /// <summary>Panel reviewer this session belongs to; null for the interactive review session.</summary>
    public string? Reviewer { get; set; }

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

public static class ResultStatus
{
    /// <summary>The agent answered with a valid report.</summary>
    public const string Ok = "ok";

    /// <summary>The agent answered, but not in the standard format.</summary>
    public const string Invalid = "invalid";

    /// <summary>The agent could not be run or failed.</summary>
    public const string Failed = "failed";
}

/// <summary>One execution of the reviewer panel over a PR.</summary>
public sealed class ReviewRun
{
    public long Id { get; set; }
    public long PrId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? HeadCommit { get; set; }
    public string? Verdict { get; set; }

    /// <summary>Branch and commit of the managed clone the reviewers read; null with a local checkout or diff only.</summary>
    public string? BaseBranch { get; set; }

    public string? BaseCommit { get; set; }

    /// <summary>Joint analysis of a PR group this run was derived from; null for a run of the PR alone.</summary>
    public long? GroupRunId { get; set; }
}

/// <summary>What one reviewer of a run produced.</summary>
public sealed class ReviewerResult
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public required string Reviewer { get; set; }
    public required string Agent { get; set; }
    public long? SessionId { get; set; }
    public required string Status { get; set; }

    /// <summary>Normalized <c>ReviewerReport</c> JSON when <see cref="Status"/> is ok.</summary>
    public string? ReportJson { get; set; }

    public string? RawOutput { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
}

public sealed record RunHistory(ReviewRun Run, IReadOnlyList<ReviewerResult> Results);

/// <summary>Everything known about one PR, as needed for display and export.</summary>
public sealed record PrHistory(
    PullRequest Pr,
    IReadOnlyList<ReviewSession> Sessions,
    IReadOnlyList<Round> Rounds,
    IReadOnlyList<Memo> Memos,
    IReadOnlyList<string> Tags,
    IReadOnlyList<RunHistory>? Runs = null,
    IReadOnlyList<PrGroup>? Groups = null)
{
    public IReadOnlyList<RunHistory> Runs { get; } = Runs ?? [];

    /// <summary>PR groups this PR belongs to (features spanning several repos).</summary>
    public IReadOnlyList<PrGroup> Groups { get; } = Groups ?? [];

    /// <summary>Sessions of the interactive review (not the panel reviewers).</summary>
    public IEnumerable<ReviewSession> ReviewSessions => Sessions.Where(s => s.Reviewer is null);
}

/// <summary>PRs of several repos that implement one feature and are reviewed together.</summary>
public sealed class PrGroup
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public string? Verdict { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One joint analysis of a PR group. Its reviewers see every PR of the group at once.</summary>
public sealed class GroupRun
{
    public long Id { get; set; }
    public long GroupId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Verdict { get; set; }

    /// <summary>The PRs analysed, as <c>ws/repo#n@head</c> separated by spaces.</summary>
    public string Members { get; set; } = "";
}

/// <summary>Agent session of one reviewer over a PR group.</summary>
public sealed class GroupSession
{
    public long Id { get; set; }
    public long GroupId { get; set; }
    public required string Reviewer { get; set; }
    public required string Agent { get; set; }
    public required string AgentSessionId { get; set; }
    public string Cwd { get; set; } = "";

    /// <summary>PR keys the session was started with; a session is not resumed once they change.</summary>
    public string Members { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}

/// <summary>Results reuse <see cref="ReviewerResult"/>: <see cref="ReviewerResult.RunId"/> is the group run.</summary>
public sealed record GroupRunHistory(GroupRun Run, IReadOnlyList<ReviewerResult> Results);
