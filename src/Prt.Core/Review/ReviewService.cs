using Prt.Core.Agents;
using Prt.Core.Bitbucket;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Export;
using Prt.Core.Repos;
using Prt.Core.Storage;

namespace Prt.Core.Review;

public interface IStatusUi
{
    void Info(string message);
    void Warn(string message);
}

/// <summary>Terminal interactions the review flow needs; implemented with Spectre.Console in the CLI.</summary>
public interface IReviewUi : IStatusUi
{
    bool Confirm(string question, bool defaultValue);

    /// <summary>Asks for a session id when it could not be detected automatically.</summary>
    string? AskSessionId(string agent);

    /// <summary>Asks for the reviewer's own note; null or empty when skipped.</summary>
    string? AskNote();
}

/// <param name="Reviewer">Panel reviewer whose session to reopen; null for the interactive review session.</param>
public sealed record ReviewOptions(
    string Agent,
    bool ForceNewSession = false,
    bool Summary = true,
    bool AskNote = true,
    string? ExtraPrompt = null,
    string? Reviewer = null);

public sealed record ReviewOutcome(PullRequest Pr, ReviewSession Session, Round Round, Memo? Summary, Memo? Note, string? NotePath);

public sealed class ReviewService(
    PrtConfig config,
    SqliteStore store,
    IPullRequestSource bitbucket,
    Func<string, IAgentAdapter> agents,
    ObsidianExporter? exporter,
    IReviewUi ui,
    TimeProvider? clock = null,
    ICheckoutProvider? checkouts = null)
{
    private readonly ICheckoutProvider _checkouts = checkouts ?? new LocalCheckouts(config);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly PullRequestSync _sync = new(store, bitbucket, ui);

    public async Task<ReviewOutcome> ReviewAsync(PrRef prRef, ReviewOptions options, CancellationToken ct = default)
    {
        var adapter = agents(options.Agent);
        var (pr, commits) = await RefreshAsync(prRef, ct);
        var head = commits.FirstOrDefault()?.Hash ?? pr.LastReviewedCommit;
        var cwd = (await _checkouts.ResolveAsync(pr, ui, ct))?.Path ?? Directory.GetCurrentDirectory();

        var session = options.ForceNewSession ? null : store.GetDefaultSession(pr.Id, options.Agent, options.Reviewer);
        if (session is null && options.Reviewer is not null)
            throw new ArgumentException(
                $"No hay sesión de {options.Agent} para el revisor '{options.Reviewer}' en {pr.Key}. Corré `prt analyze {pr.Key}` primero.");
        var startedAt = _clock.GetUtcNow();
        string? prompt;
        string kind;

        if (session is null)
        {
            kind = RoundKinds.New;
            prompt = Prompts.Render(config.Prompts.Initial, pr);
            if (options.ExtraPrompt is { Length: > 0 } extra)
                prompt += "\n\n" + extra;

            ui.Info($"Iniciando sesión nueva de {options.Agent} para {pr.Key} en {cwd}");
            var sessionId = await adapter.StartAsync(prompt, cwd, ct);
            if (sessionId is null)
            {
                ui.Warn($"No se pudo detectar el id de la sesión de {options.Agent}.");
                sessionId = ui.AskSessionId(options.Agent);
            }
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new AgentException(
                    $"Sin id de sesión no se puede registrar la revisión. Asocialo luego con: prt link {pr.Key} --agent {options.Agent} --session <id>");

            session = store.AddSession(pr.Id, options.Agent, sessionId.Trim(), cwd);
        }
        else
        {
            kind = RoundKinds.Resume;
            prompt = ReReviewPrompt(pr, commits);
            if (prompt is not null && !ui.Confirm("¿Pedirle al agente que revise los commits nuevos?", true))
                prompt = null;
            if (options.ExtraPrompt is { Length: > 0 } extra)
                prompt = prompt is null ? extra : prompt + "\n\n" + extra;

            if (!string.IsNullOrEmpty(session.Cwd) && Directory.Exists(session.Cwd))
                cwd = session.Cwd;
            ui.Info($"Retomando sesión {session.AgentSessionId} de {options.Agent} para {pr.Key}");
            await adapter.ResumeAsync(session.AgentSessionId, cwd, prompt, ct);
            store.TouchSession(session.Id);
        }

        var round = store.AddRound(session.Id, kind, startedAt, _clock.GetUtcNow(), head, prompt);

        Memo? summary = null;
        if (options.Summary && ui.Confirm("¿Generar el memo de la sesión con el agente?", true))
        {
            ui.Info("Pidiendo resumen al agente…");
            try
            {
                var text = await adapter.AskAsync(session.AgentSessionId, cwd, config.Prompts.Summary, ct);
                summary = store.AddMemo(pr.Id, round.Id, MemoKinds.AgentSummary, text);
            }
            catch (AgentException ex)
            {
                ui.Warn($"No se pudo generar el resumen: {ex.Message}");
            }
        }

        Memo? note = null;
        if (options.AskNote && ui.AskNote() is { Length: > 0 } noteText && noteText.Trim().Length > 0)
            note = store.AddMemo(pr.Id, round.Id, MemoKinds.UserNote, noteText);

        store.SetReviewState(pr.Id, head, summary is null ? null : MemoText.Verdict(summary.Body));
        var notePath = Export(pr.Id);
        return new ReviewOutcome(store.GetPullRequest(pr.Id)!, session, round, summary, note, notePath);
    }

    /// <summary>Fetches PR metadata from Bitbucket and stores it; falls back to the stored copy when offline.</summary>
    public Task<(PullRequest Pr, IReadOnlyList<PrCommit> Commits)> RefreshAsync(PrRef prRef, CancellationToken ct = default) =>
        _sync.RefreshAsync(prRef, ct);

    public string? Export(long prId) => exporter?.Export(store, prId);

    public void ExportIndex() => exporter?.ExportIndex(store);

    private string? ReReviewPrompt(PullRequest pr, IReadOnlyList<PrCommit> commits)
    {
        if (pr.LastReviewedCommit is null || commits.Count == 0 || CommitHashes.Same(commits[0].Hash, pr.LastReviewedCommit))
            return null;

        // Unknown base (force push / rebase): all commits are listed.
        var added = CommitHashes.Since(commits, pr.LastReviewedCommit) ?? commits;
        if (added.Count == 0)
            return null;

        ui.Info($"El PR tiene {added.Count} commit(s) nuevo(s) desde la última revisión ({CommitHashes.Short(pr.LastReviewedCommit)}).");
        var list = string.Join('\n', added.Select(c => $"- {c.ShortHash} {c.Subject}"));
        return Prompts.Render(config.Prompts.ReReview, pr, new Dictionary<string, string>
        {
            ["count"] = added.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["fromCommit"] = CommitHashes.Short(pr.LastReviewedCommit),
            ["toCommit"] = commits[0].ShortHash,
            ["commits"] = list,
        });
    }
}

public static class Prompts
{
    public static string Render(string template, PullRequest pr, IReadOnlyDictionary<string, string>? extra = null)
    {
        var values = new Dictionary<string, string>
        {
            ["key"] = pr.Key,
            ["url"] = pr.Url,
            ["title"] = pr.Title,
            ["author"] = pr.Author,
            ["source"] = pr.SourceBranch,
            ["destination"] = pr.DestinationBranch,
            ["description"] = pr.Description.Trim().Length > 0 ? pr.Description.Trim() : "(sin descripción)",
        };
        if (extra is not null)
        {
            foreach (var (k, v) in extra)
                values[k] = v;
        }

        // Single pass, so placeholders inside PR text (e.g. the description) are left alone.
        return System.Text.RegularExpressions.Regex
            .Replace(template, @"\{(\w+)\}", m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value)
            .Trim();
    }
}
