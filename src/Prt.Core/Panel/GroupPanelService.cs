using System.Globalization;
using Prt.Core.Agents;
using Prt.Core.Bitbucket;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Export;
using Prt.Core.Repos;
using Prt.Core.Review;
using Prt.Core.Storage;

namespace Prt.Core.Panel;

public sealed record GroupOutcome(
    PrGroup Group,
    GroupRun Run,
    IReadOnlyList<ReviewerOutcome> Reviewers,
    Verdict? Verdict,
    IReadOnlyDictionary<string, Verdict?> PrVerdicts,
    string? NotePath);

/// <summary>
/// Joint analysis of a PR group: each reviewer sees every PR of the group in one session (plus the coordination
/// reviewer), answers with findings tagged by PR, and each PR gets a run derived from its share of the findings.
/// </summary>
public sealed class GroupPanelService(
    PrtConfig config,
    SqliteStore store,
    IPullRequestSource bitbucket,
    Func<string, IAgentAdapter> agents,
    ObsidianExporter? exporter,
    IPanelUi ui,
    string runsRoot,
    TimeProvider? clock = null,
    Func<string, string?, ReviewProfile>? loadProfile = null,
    Func<ReviewProfile>? loadGlobalProfile = null,
    ICheckoutProvider? checkouts = null)
{
    private readonly ICheckoutProvider _checkouts = checkouts ?? new LocalCheckouts(config);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _storeLock = new();

    public async Task<GroupOutcome> AnalyzeAsync(long groupId, PanelOptions options, CancellationToken ct = default)
    {
        var group = store.GetGroup(groupId) ?? throw new ArgumentException($"El grupo {groupId} no existe.");
        var members = store.ListGroupMembers(groupId);
        if (members.Count < 2)
            throw new ArgumentException($"El grupo «{group.Name}» necesita al menos 2 PRs para un análisis conjunto.");

        // Reviewers and standards are the global ones: the group spans projects. Each project still adds its context.
        var profile = (loadGlobalProfile ?? (() => ProfileLoader.Load([ProfileLoader.GlobalDir])))();
        var reviewers = options.AllWhenEmpty && options.Reviewers is not { Count: > 0 }
            ? profile.SelectForGroup(null).Concat(profile.Reviewers.Concat(profile.GroupReviewers).Where(r => !r.Enabled))
                .DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : profile.SelectForGroup(options.Reviewers);
        if (reviewers.Count == 0)
            throw new ArgumentException("No hay revisores habilitados para el análisis conjunto.");

        var previousRun = store.ListGroupRuns(groupId).LastOrDefault()?.Run;
        var previousHeads = ParseMembers(previousRun?.Members);

        var synced = new List<(PullRequest Pr, IReadOnlyList<PrCommit> Commits, string? Head, CheckoutInfo? Code)>();
        foreach (var member in members)
        {
            ui.Info($"{member.Key}: consultando Bitbucket y preparando el código…");
            var (pr, commits) = await new PullRequestSync(store, bitbucket, ui).RefreshAsync(member.Ref, ct);
            var code = await _checkouts.ResolveAsync(pr, ui, ct);
            synced.Add((pr, commits, commits.FirstOrDefault()?.Hash ?? pr.LastReviewedCommit, code));
        }

        var membersText = string.Join(' ', synced.Select(m => m.Head is null ? m.Pr.Key : $"{m.Pr.Key}@{m.Head}"));
        var run = store.StartGroupRun(groupId, membersText, _clock.GetUtcNow());
        var context = await WriteContextAsync(group, run, synced, previousHeads, ct);
        var membersKey = MembersKey(context.PrKeys);
        var readDirs = context.CodeDirs;

        using var gate = new SemaphoreSlim(Math.Clamp(options.MaxParallel ?? profile.MaxParallel, 1, 16));
        var outcomes = await Task.WhenAll(reviewers.Select(async reviewer =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await RunReviewerAsync(reviewer, profile, group, run, context, membersKey, readDirs, options, ct);
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var outcome in outcomes)
            store.AddGroupResult(outcome.Result);

        var reports = outcomes.Select(o => o.Report).OfType<ReviewerReport>().ToList();
        Verdict? verdict = reports.Count == 0 ? null : ReviewStandard.Combine(reports.Select(r => r.Verdict));
        var label = verdict is { } v ? ReviewStandard.Label(v) : null;
        store.FinishGroupRun(run.Id, label);
        store.SetGroupVerdict(groupId, label);

        var prVerdicts = new Dictionary<string, Verdict?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (member, (pr, _, head, code)) in context.Members.Zip(synced))
            prVerdicts[pr.Key] = await DeriveRunAsync(pr, head, code, member, run, outcomes, ct);

        string? notePath = null;
        if (exporter is not null)
        {
            foreach (var (pr, _, _, _) in synced)
                exporter.Export(store, pr.Id);
            notePath = exporter.ExportGroup(store, groupId);
        }
        var finished = store.ListGroupRuns(groupId).Last().Run;
        return new GroupOutcome(store.GetGroup(groupId)!, finished, outcomes, verdict, prVerdicts, notePath);
    }

    /// <summary>PR keys a group session was started with, so it is not resumed once the group changes.</summary>
    public static string MembersKey(IEnumerable<string> prKeys) =>
        string.Join(' ', prKeys.Select(k => k.ToLowerInvariant()).Order(StringComparer.Ordinal));

    /// <summary>PR key → head commit, from <see cref="GroupRun.Members"/>.</summary>
    public static IReadOnlyDictionary<string, string?> ParseMembers(string? members) =>
        (members ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.Split('@', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : null, StringComparer.OrdinalIgnoreCase);

    private async Task<ReviewerOutcome> RunReviewerAsync(ReviewerDefinition reviewer, ReviewProfile profile, PrGroup group, GroupRun run,
        GroupRunContext context, string membersKey, IReadOnlyList<string> readDirs, PanelOptions options, CancellationToken ct)
    {
        var agentName = (options.Agent ?? reviewer.Agent ?? profile.DefaultAgent ?? config.DefaultAgent).ToLowerInvariant();
        GroupSession? existing;
        bool hasPrevious;
        lock (_storeLock)
        {
            existing = options.ForceNewSessions ? null : store.GetGroupSession(group.Id, reviewer.Name, agentName);
            hasPrevious = File.Exists(context.PreviousPath(reviewer.Name));
        }
        // A session that saw another set of PRs would mix them up: start over when the group changed.
        if (existing is not null && existing.Members != membersKey)
        {
            ui.Info($"{reviewer.Title}: el grupo cambió desde la última sesión; se empieza una nueva.");
            existing = null;
        }

        var prompt = existing is null
            ? GroupPrompts.Initial(reviewer, profile, context, hasPrevious)
            : GroupPrompts.FollowUp(reviewer, context, hasPrevious);

        ui.ReviewerStarted(reviewer, agentName, existing is not null);
        var started = _clock.GetUtcNow();
        var warnings = new List<string>();
        var session = existing;
        ReviewerReport? report = null;
        string status;
        string? error = null;
        string? raw = null;

        try
        {
            var adapter = agents(agentName);
            if (adapter is ClaudeAdapter { SupportsReadDirs: false })
                warnings.Add($"La plantilla de {agentName} del config no tiene {AgentAdapterBase.ReadDirsPlaceholder}: puede no leer el código de los otros repos.");

            var (parsed, answer) = await ReviewerCall.RunAsync(adapter, existing?.AgentSessionId, prompt, context.Dir, context.Dir, readDirs,
                reported =>
                {
                    session = SaveSession(group, reviewer, agentName, existing, reported, context.Dir, membersKey, warnings);
                    return session?.AgentSessionId;
                },
                text => ReportParser.Parse(text, reviewer.Name, reviewer.IdPrefix, context.PrKeys),
                errors => GroupPrompts.FixFormat(reviewer, context, errors), ct);
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

        foreach (var warning in warnings)
            ui.Warn($"{reviewer.Title}: {warning}");

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

    private GroupSession? SaveSession(PrGroup group, ReviewerDefinition reviewer, string agent, GroupSession? existing, string? sessionId,
        string cwd, string membersKey, List<string> warnings)
    {
        lock (_storeLock)
        {
            if (existing is not null && (sessionId is null || sessionId == existing.AgentSessionId))
            {
                store.TouchGroupSession(existing.Id);
                return existing;
            }
            if (sessionId is null)
            {
                warnings.Add($"{agent} no informó el id de sesión: la próxima pasada empezará una sesión nueva.");
                return null;
            }
            return store.AddGroupSession(group.Id, reviewer.Name, agent, sessionId, cwd, membersKey);
        }
    }

    /// <summary>
    /// Stores, for one PR, a regular panel run with its share of the joint findings, so the PR's pages, verdict
    /// and note show it like any other analysis.
    /// </summary>
    private async Task<Verdict?> DeriveRunAsync(PullRequest pr, string? head, CheckoutInfo? code, GroupMemberContext member,
        GroupRun groupRun, IReadOnlyList<ReviewerOutcome> outcomes, CancellationToken ct)
    {
        var run = store.StartRun(pr.Id, head, groupRun.StartedAt, code?.BaseBranch, code?.BaseCommit, groupRun.Id);
        if (member.HasDiff)
        {
            var target = UnifiedDiff.RunDiffPath(runsRoot, pr.Ref, run.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, await File.ReadAllTextAsync(member.DiffPath, ct), ct);
        }

        var reports = new List<ReviewerReport>();
        foreach (var outcome in outcomes)
        {
            var share = outcome.Report?.ForPr(pr.Key);
            if (share is not null)
                reports.Add(share);
            store.AddResult(new ReviewerResult
            {
                RunId = run.Id,
                Reviewer = outcome.Result.Reviewer,
                Agent = outcome.Result.Agent,
                Status = outcome.Result.Status,
                ReportJson = share?.ToJson(),
                RawOutput = share is null ? outcome.Result.RawOutput : null,
                Error = outcome.Result.Error,
                StartedAt = outcome.Result.StartedAt,
                EndedAt = outcome.Result.EndedAt,
            });
        }

        Verdict? verdict = reports.Count == 0 ? null : ReviewStandard.Combine(reports.Select(r => r.Verdict));
        var label = verdict is { } v ? ReviewStandard.Label(v) : null;
        store.FinishRun(run.Id, label);
        if (label is not null)
            store.SetReviewState(pr.Id, head, label);
        return verdict;
    }

    private async Task<GroupRunContext> WriteContextAsync(PrGroup group, GroupRun run,
        IReadOnlyList<(PullRequest Pr, IReadOnlyList<PrCommit> Commits, string? Head, CheckoutInfo? Code)> synced,
        IReadOnlyDictionary<string, string?> previousHeads, CancellationToken ct)
    {
        var dir = Path.Combine(runsRoot, "groups", group.Id.ToString(CultureInfo.InvariantCulture), run.Id.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);

        var members = new List<GroupMemberContext>();
        foreach (var (pr, commits, head, code) in synced)
        {
            var memberDir = Path.Combine(dir, "prs", $"{ObsidianExporter.SafeName(pr.Workspace)}-{ObsidianExporter.SafeName(pr.Repo)}-{pr.Number}");
            Directory.CreateDirectory(memberDir);

            var hasDiff = false;
            try
            {
                await File.WriteAllTextAsync(Path.Combine(memberDir, "diff.patch"), await bitbucket.GetDiffAsync(pr.Ref, ct), ct);
                hasDiff = true;
            }
            catch (Exception ex) when (ex is BitbucketException or HttpRequestException)
            {
                ui.Warn($"{pr.Key}: no se pudo obtener el diff: {ex.Message}");
            }

            ReviewProfile? project = null;
            try
            {
                project = (loadProfile ?? ProfileLoader.Load)(pr.Ref.RepoKey, code?.Path);
            }
            catch (ArgumentException ex)
            {
                ui.Warn($"{pr.Ref.RepoKey}: configuración de revisores inválida, se omite su contexto ({ex.Message}).");
            }

            var newCommits = previousHeads.TryGetValue(pr.Key, out var previousHead) && previousHead is not null
                ? CommitHashes.Since(commits, previousHead)
                : null;
            var member = new GroupMemberContext(pr, head, newCommits, code, memberDir, hasDiff,
                project is { Context.Length: > 0 }, project?.ContextFiles ?? []);
            await File.WriteAllTextAsync(member.PrPath, PanelPrompts.PrFile(pr, commits), ct);
            if (member.HasProjectContext)
                await File.WriteAllTextAsync(member.ContextPath, project!.Context, ct);
            members.Add(member);
        }

        var context = new GroupRunContext(dir, group, members);
        await File.WriteAllTextAsync(context.GroupPath, GroupPrompts.GroupFile(group, members), ct);

        foreach (var reviewer in store.ListGroupRuns(group.Id).SelectMany(r => r.Results).Select(r => r.Reviewer).Distinct())
        {
            if (store.LatestOkGroupResult(group.Id, reviewer)?.ReportJson is { } previous)
                await File.WriteAllTextAsync(context.PreviousPath(reviewer), previous, ct);
        }
        return context;
    }
}
