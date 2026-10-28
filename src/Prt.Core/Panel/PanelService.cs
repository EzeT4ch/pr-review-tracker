using Prt.Core.Agents;
using Prt.Core.Bitbucket;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Export;
using Prt.Core.Repos;
using Prt.Core.Review;
using Prt.Core.Storage;

namespace Prt.Core.Panel;

public interface IPanelUi : IStatusUi
{
    void ReviewerStarted(ReviewerDefinition reviewer, string agent, bool resumed);
    void ReviewerFinished(ReviewerOutcome outcome);
}

public sealed record PanelOptions(
    IReadOnlyCollection<string>? Reviewers = null,
    string? Agent = null,
    bool ForceNewSessions = false,
    int? MaxParallel = null,
    bool AllWhenEmpty = false);

public sealed record ReviewerOutcome(
    ReviewerDefinition Reviewer,
    ReviewerResult Result,
    ReviewerReport? Report,
    IReadOnlyList<string> Warnings);

public sealed record PanelOutcome(
    PullRequest Pr,
    ReviewRun Run,
    IReadOnlyList<ReviewerOutcome> Reviewers,
    Verdict? Verdict,
    string? NotePath);

/// <summary>
/// Runs the reviewer panel over a PR: one headless agent session per reviewer, in parallel,
/// all answering in the standard report format, then consolidates and exports the result.
/// </summary>
public sealed class PanelService(
    PrtConfig config,
    SqliteStore store,
    IPullRequestSource bitbucket,
    Func<string, IAgentAdapter> agents,
    ObsidianExporter? exporter,
    IPanelUi ui,
    string runsRoot,
    TimeProvider? clock = null,
    Func<string, string?, ReviewProfile>? loadProfile = null,
    ICheckoutProvider? checkouts = null)
{
    private readonly ICheckoutProvider _checkouts = checkouts ?? new LocalCheckouts(config);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    // SqliteStore wraps a single connection: reviewers finishing concurrently take turns.
    private readonly Lock _storeLock = new();

    public async Task<PanelOutcome> AnalyzeAsync(PrRef prRef, PanelOptions options, CancellationToken ct = default)
    {
        var (pr, commits) = await new PullRequestSync(store, bitbucket, ui).RefreshAsync(prRef, ct);
        var head = commits.FirstOrDefault()?.Hash ?? pr.LastReviewedCommit;
        var code = await _checkouts.ResolveAsync(pr, ui, ct);
        var checkout = code?.Path;

        var profile = (loadProfile ?? ProfileLoader.Load)(prRef.RepoKey, checkout);
        // The web panel runs every defined reviewer when none is picked; the CLI keeps the enabled ones.
        var reviewers = options.AllWhenEmpty && options.Reviewers is not { Count: > 0 }
            ? profile.Reviewers
            : profile.Select(options.Reviewers);
        if (reviewers.Count == 0)
            throw new ArgumentException("No hay revisores habilitados para este proyecto (revisá review.json o los archivos de revisores).");

        var previousRun = store.ListRuns(pr.Id).LastOrDefault();
        var run = store.StartRun(pr.Id, head, _clock.GetUtcNow(), code?.BaseBranch, code?.BaseCommit);
        var context = await WriteContextAsync(pr, commits, profile, code, run, ct);
        var newCommits = previousRun?.Run.HeadCommit is { } previousHead ? CommitHashes.Since(commits, previousHead) : null;

        using var gate = new SemaphoreSlim(Math.Clamp(options.MaxParallel ?? profile.MaxParallel, 1, 16));
        var outcomes = await Task.WhenAll(reviewers.Select(async reviewer =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await RunReviewerAsync(reviewer, profile, pr, head, newCommits, run, context, checkout ?? Directory.GetCurrentDirectory(), options, ct);
            }
            finally
            {
                gate.Release();
            }
        }));

        // Results are stored in profile order (not completion order), so every report lists reviewers the same way.
        foreach (var outcome in outcomes)
            store.AddResult(outcome.Result);

        var reports = outcomes.Select(o => o.Report).OfType<ReviewerReport>().ToList();
        Verdict? verdict = reports.Count == 0 ? null : ReviewStandard.Combine(reports.Select(r => r.Verdict));
        var label = verdict is { } v ? ReviewStandard.Label(v) : null;
        store.FinishRun(run.Id, label);
        if (label is not null)
            store.SetReviewState(pr.Id, head, label);

        var notePath = exporter?.Export(store, pr.Id);
        return new PanelOutcome(store.GetPullRequest(pr.Id)!, store.ListRuns(pr.Id).Last().Run, outcomes, verdict, notePath);
    }

    private async Task<ReviewerOutcome> RunReviewerAsync(ReviewerDefinition reviewer, ReviewProfile profile, PullRequest pr,
        string? head, IReadOnlyList<PrCommit>? newCommits, ReviewRun run, RunContext context, string cwd, PanelOptions options,
        CancellationToken ct)
    {
        var agentName = (options.Agent ?? reviewer.Agent ?? profile.DefaultAgent ?? config.DefaultAgent).ToLowerInvariant();
        ReviewSession? existing;
        bool hasPrevious;
        lock (_storeLock)
        {
            existing = options.ForceNewSessions ? null : store.GetDefaultSession(pr.Id, agentName, reviewer.Name);
            hasPrevious = File.Exists(context.PreviousPath(reviewer.Name));
        }

        var prompt = existing is null
            ? PanelPrompts.Initial(reviewer, profile, pr, head, context, hasPrevious)
            : PanelPrompts.FollowUp(reviewer, pr, head, newCommits, context, hasPrevious);

        ui.ReviewerStarted(reviewer, agentName, existing is not null);
        var started = _clock.GetUtcNow();
        var warnings = new List<string>();
        ReviewSession? session = existing;
        ReviewerReport? report = null;
        string status;
        string? error = null;
        string? raw = null;

        try
        {
            var (parsed, answer) = await ReviewerCall.RunAsync(agents(agentName), existing?.AgentSessionId, prompt, cwd, context.Dir, null,
                reported =>
                {
                    session = SaveSession(pr, agentName, reviewer, existing, reported, cwd, warnings);
                    return session?.AgentSessionId;
                },
                text => ReportParser.Parse(text, reviewer.Name, reviewer.IdPrefix),
                errors => PanelPrompts.FixFormat(reviewer, errors), ct);
            raw = answer;

            warnings.AddRange(parsed.Warnings);
            report = parsed.Report;
            status = report is null ? ResultStatus.Invalid : ResultStatus.Ok;
            if (report is null)
                error = string.Join("; ", parsed.Errors);
        }
        catch (Exception ex) when (ex is AgentException or InvalidOperationException or ArgumentException)
        {
            status = ResultStatus.Failed;
            error = ex.Message;
        }

        var result = new ReviewerResult
        {
            RunId = run.Id,
            Reviewer = reviewer.Name,
            Agent = agentName,
            SessionId = session?.Id,
            Status = status,
            ReportJson = report?.ToJson(),
            RawOutput = raw,
            Error = error,
            StartedAt = started,
            EndedAt = _clock.GetUtcNow(),
        };

        var outcome = new ReviewerOutcome(reviewer, result, report, warnings);
        ui.ReviewerFinished(outcome);
        return outcome;
    }

    private ReviewSession? SaveSession(PullRequest pr, string agent, ReviewerDefinition reviewer, ReviewSession? existing,
        string? sessionId, string cwd, List<string> warnings)
    {
        lock (_storeLock)
        {
            if (existing is not null && (sessionId is null || sessionId == existing.AgentSessionId))
            {
                store.TouchSession(existing.Id);
                return existing;
            }
            if (sessionId is null)
            {
                warnings.Add($"{agent} no informó el id de sesión: la próxima pasada empezará una sesión nueva.");
                return null;
            }
            return store.AddSession(pr.Id, agent, sessionId, cwd, label: reviewer.Title, reviewer: reviewer.Name);
        }
    }

    private async Task<RunContext> WriteContextAsync(PullRequest pr, IReadOnlyList<PrCommit> commits, ReviewProfile profile,
        CheckoutInfo? code, ReviewRun run, CancellationToken ct)
    {
        // Kept after the run: the web panel shows the exact diff each run analysed.
        var dir = Path.GetDirectoryName(UnifiedDiff.RunDiffPath(runsRoot, pr.Ref, run.Id))!;
        Directory.CreateDirectory(dir);

        var hasDiff = false;
        try
        {
            var diff = await bitbucket.GetDiffAsync(pr.Ref, ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "diff.patch"), diff, ct);
            hasDiff = true;
        }
        catch (Exception ex) when (ex is BitbucketException or HttpRequestException)
        {
            ui.Warn($"No se pudo obtener el diff del PR: {ex.Message}");
        }

        var hasComments = await PanelMaterial.WriteCommentsAsync(bitbucket, pr, Path.Combine(dir, "comments.md"), ui, ct);
        var context = new RunContext(dir, hasDiff, profile.Context.Length > 0, code, profile.ContextFiles, hasComments);
        await File.WriteAllTextAsync(context.PrPath, PanelPrompts.PrFile(pr, commits), ct);
        if (context.HasProjectContext)
            await File.WriteAllTextAsync(context.ContextPath, profile.Context, ct);

        foreach (var reviewer in profile.Reviewers)
        {
            if (store.LatestOkResult(pr.Id, reviewer.Name)?.ReportJson is { } previous)
                await File.WriteAllTextAsync(context.PreviousPath(reviewer.Name), previous, ct);
        }
        return context;
    }
}

/// <summary>Material for the reviewers shared by the PR panel and the joint analysis.</summary>
internal static class PanelMaterial
{
    /// <summary>Writes the PR's comments for the reviewers; false when there are none or they cannot be read.</summary>
    public static async Task<bool> WriteCommentsAsync(IPullRequestSource bitbucket, PullRequest pr, string path, IStatusUi ui, CancellationToken ct)
    {
        try
        {
            var comments = await bitbucket.GetCommentsAsync(pr.Ref, ct);
            if (comments.Count == 0)
                return false;
            await File.WriteAllTextAsync(path, CommentService.CommentsFile(pr, comments), ct);
            return true;
        }
        catch (Exception ex) when (ex is BitbucketException or HttpRequestException)
        {
            ui.Warn($"{pr.Key}: no se pudieron leer los comentarios del PR ({ex.Message}); los revisores no los van a ver.");
            return false;
        }
    }
}

/// <summary>One reviewer turn: ask the agent, parse the report and, if it is out of format, ask once more in the same session.</summary>
internal static class ReviewerCall
{
    /// <param name="onSession">Stores the session the agent reported and returns the id to continue it, or null if it cannot be continued.</param>
    public static async Task<(ParseResult Parsed, string Raw)> RunAsync(IAgentAdapter adapter, string? sessionId, string prompt, string cwd,
        string contextDir, IReadOnlyList<string>? readDirs, Func<string?, string?> onSession, Func<string, ParseResult> parse,
        Func<IReadOnlyList<string>, string> fixFormat, CancellationToken ct)
    {
        var answer = await adapter.RunReviewAsync(sessionId, prompt, cwd, contextDir, ct, readDirs);
        var raw = answer.Answer;
        var continueId = onSession(answer.SessionId);

        var parsed = parse(answer.Answer);
        if (!parsed.Success && continueId is not null)
        {
            // One correction round inside the same session, so the agent keeps its analysis.
            var retry = await adapter.RunReviewAsync(continueId, fixFormat(parsed.Errors), cwd, contextDir, ct, readDirs);
            raw += "\n\n---- reintento de formato ----\n\n" + retry.Answer;
            parsed = parse(retry.Answer);
        }
        return (parsed, raw);
    }
}
