using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;
using Prt.Core.Panel;

namespace Prt.Core.Export;

/// <summary>
/// Writes one Markdown note per PR plus an index into an Obsidian vault. The generated part of a
/// note lives between <see cref="BeginMarker"/> and <see cref="EndMarker"/>; anything the user writes
/// outside of it is preserved when the note is regenerated. The frontmatter is always regenerated.
/// </summary>
public sealed partial class ObsidianExporter
{
    public const string BeginMarker = "<!-- prt:begin -->";
    public const string EndMarker = "<!-- prt:end -->";
    private const string DefaultUserSection = "\n\n## Notas libres\n\n";

    private readonly string _vaultPath;
    private readonly string _folder;
    private readonly Func<string, string, string> _resumeCommand;
    private readonly TimeZoneInfo _timeZone;

    /// <param name="resumeCommand">(agent, sessionId) → shell command that reopens the session.</param>
    public ObsidianExporter(string vaultPath, string folder, Func<string, string, string> resumeCommand, TimeZoneInfo? timeZone = null)
    {
        _vaultPath = vaultPath;
        _folder = folder.Trim('/', '\\');
        _resumeCommand = resumeCommand;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public string RootDir => Path.Combine(_vaultPath, _folder);
    public string IndexPath => Path.Combine(RootDir, "Index.md");

    public string NoteDirectory(PullRequest pr) => Path.Combine(RootDir, SafeName(pr.Workspace), SafeName(pr.Repo));

    public string NoteFileName(PullRequest pr)
    {
        var title = SafeName(pr.Title);
        if (title.Length > 80)
            title = title[..80].TrimEnd(' ', '.');
        return title.Length == 0 ? $"PR-{pr.Number}.md" : $"PR-{pr.Number} - {title}.md";
    }

    /// <summary>Writes (or regenerates) the note of a PR and returns its path.</summary>
    public string ExportPr(PrHistory history)
    {
        var pr = history.Pr;
        var dir = NoteDirectory(pr);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, NoteFileName(pr));

        // The title may have changed since the last export: rename instead of duplicating.
        var existing = FindExistingNote(dir, pr.Number);
        if (existing is not null && !string.Equals(existing, path, StringComparison.Ordinal))
            File.Move(existing, path, overwrite: true);

        var previous = File.Exists(path) ? File.ReadAllText(path) : null;
        WriteAtomic(path, RenderNote(history, previous));

        foreach (var run in history.Runs)
            ExportRun(pr, RunView.From(run));
        return path;
    }

    /// <summary>Writes the full report of one panel run, next to the PR note.</summary>
    public string ExportRun(PullRequest pr, RunView view)
    {
        var dir = RunDirectory(pr);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, RunFileName(view.Run) + ".md");
        var previous = File.Exists(path) ? File.ReadAllText(path) : null;
        var (before, after) = UserContent(previous);

        var sb = new StringBuilder("---\n");
        Yaml(sb, "pr", pr.Key);
        sb.Append("run: ").Append(view.Run.Id).Append('\n');
        Yaml(sb, "date", Local(view.Run.StartedAt).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
        Yaml(sb, "verdict", view.Run.Verdict ?? "");
        Yaml(sb, "head_commit", CommitHashes.Short(view.Run.HeadCommit));
        sb.Append("reviewers:");
        AppendList(sb, view.Reviewers.Select(r => r.Result.Reviewer));
        sb.Append("blockers: ").Append(view.Count(Severity.Blocker)).Append('\n');
        sb.Append("majors: ").Append(view.Count(Severity.Major)).Append('\n');
        sb.Append("open_findings: ").Append(view.OpenFindings.Count()).Append('\n');
        sb.Append("tags:\n  - \"pr-review-analysis\"\n---\n");
        sb.Append(before).Append(BeginMarker).Append('\n');
        sb.Append("# Análisis del panel · ").Append(pr.Key).Append(" · ").Append(Display(view.Run.StartedAt)).Append("\n\n");
        sb.Append("PR: [[").Append(NoteLink(pr)).Append("|").Append(pr.Key).Append(" — ").Append(pr.Title).Append("]]\n\n");
        sb.Append(ReportRenderer.Render(view, Display));
        sb.Append(EndMarker).Append(previous is null ? "\n" : after);
        if (sb[^1] != '\n')
            sb.Append('\n');
        WriteAtomic(path, sb.ToString());
        return path;
    }

    public string RunDirectory(PullRequest pr) => Path.Combine(NoteDirectory(pr), $"PR-{pr.Number}");

    private string RunFileName(ReviewRun run) =>
        $"Análisis {Local(run.StartedAt).ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)} (run {run.Id})";

    private string NoteLink(PullRequest pr) =>
        $"{_folder}/{SafeName(pr.Workspace)}/{SafeName(pr.Repo)}/{Path.GetFileNameWithoutExtension(NoteFileName(pr))}";

    private string RunLink(PullRequest pr, ReviewRun run) =>
        $"{_folder}/{SafeName(pr.Workspace)}/{SafeName(pr.Repo)}/PR-{pr.Number}/{RunFileName(run)}";

    public string ExportIndex(IReadOnlyList<PrHistory> histories)
    {
        Directory.CreateDirectory(RootDir);
        WriteAtomic(IndexPath, RenderIndex(histories));
        return IndexPath;
    }

    // ---- Note ----------------------------------------------------------------------------

    public string RenderNote(PrHistory history, string? previousContent)
    {
        var (before, after) = UserContent(previousContent);
        var sb = new StringBuilder();
        sb.Append(RenderFrontmatter(history));
        sb.Append(before);
        sb.Append(BeginMarker).Append('\n');
        sb.Append(RenderBody(history));
        sb.Append(EndMarker);
        sb.Append(after);
        if (!after.EndsWith('\n'))
            sb.Append('\n');
        return sb.ToString();
    }

    private string RenderFrontmatter(PrHistory h)
    {
        var pr = h.Pr;
        var sb = new StringBuilder("---\n");
        Yaml(sb, "pr", pr.Key);
        Yaml(sb, "url", pr.Url);
        Yaml(sb, "workspace", pr.Workspace);
        Yaml(sb, "repo", pr.Repo);
        sb.Append("number: ").Append(pr.Number).Append('\n');
        Yaml(sb, "title", pr.Title);
        Yaml(sb, "author", pr.Author);
        Yaml(sb, "state", pr.State);
        Yaml(sb, "source_branch", pr.SourceBranch);
        Yaml(sb, "destination_branch", pr.DestinationBranch);
        Yaml(sb, "verdict", pr.Verdict ?? "");
        Yaml(sb, "last_review", LastReview(h) is { } last ? Local(last).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture) : "");
        Yaml(sb, "last_reviewed_commit", CommitHashes.Short(pr.LastReviewedCommit));
        sb.Append("rounds: ").Append(h.Rounds.Count).Append('\n');

        sb.Append("agents:");
        AppendList(sb, h.Sessions.Select(s => s.Agent).Distinct());
        sb.Append("sessions:");
        if (h.Sessions.Count == 0)
            sb.Append(" []\n");
        else
        {
            sb.Append('\n');
            foreach (var s in h.Sessions)
            {
                sb.Append("  - agent: ").Append(Quote(s.Agent)).Append('\n');
                sb.Append("    id: ").Append(Quote(s.AgentSessionId)).Append('\n');
                if (s.Reviewer is not null)
                    sb.Append("    reviewer: ").Append(Quote(s.Reviewer)).Append('\n');
                sb.Append("    resume: ").Append(Quote(_resumeCommand(s.Agent, s.AgentSessionId))).Append('\n');
            }
        }
        if (h.Runs.Count > 0)
        {
            var latest = RunView.From(h.Runs[^1]);
            Yaml(sb, "panel_verdict", latest.Run.Verdict ?? "");
            Yaml(sb, "last_analysis", Local(latest.Run.StartedAt).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
            sb.Append("blockers: ").Append(latest.Count(Severity.Blocker)).Append('\n');
            sb.Append("majors: ").Append(latest.Count(Severity.Major)).Append('\n');
            sb.Append("open_findings: ").Append(latest.OpenFindings.Count()).Append('\n');
        }
        sb.Append("tags:");
        AppendList(sb, new[] { "pr-review" }.Concat(h.Tags).Distinct());
        sb.Append("---\n");
        return sb.ToString();
    }

    private string RenderBody(PrHistory h)
    {
        var pr = h.Pr;
        var sb = new StringBuilder();
        sb.Append("# PR #").Append(pr.Number).Append(" · ").Append(pr.Title.Length > 0 ? pr.Title : pr.Key).Append("\n\n");

        sb.Append("> [!info] ").Append(pr.Key).Append(" · ").Append(Or(pr.State, "sin estado")).Append('\n');
        sb.Append("> **Autor:** ").Append(Or(pr.Author, "—")).Append(" · **Ramas:** `")
            .Append(Or(pr.SourceBranch, "?")).Append("` → `").Append(Or(pr.DestinationBranch, "?")).Append("`\n");
        if (pr.Url.Length > 0)
            sb.Append("> [Abrir en Bitbucket](").Append(pr.Url).Append(")\n");
        if (pr.Verdict is { Length: > 0 })
            sb.Append("> **Veredicto:** ").Append(pr.Verdict).Append('\n');
        sb.Append('\n');
        sb.Append("Retomar la revisión: `prt review ").Append(pr.Key).Append("` · Analizar con el panel: `prt analyze ")
            .Append(pr.Key).Append("`\n\n");

        if (pr.Description.Trim().Length > 0)
        {
            sb.Append("> [!quote]- Descripción del PR\n");
            AppendQuoted(sb, MemoText.NestHeadings(pr.Description.Trim(), 4));
            sb.Append('\n');
        }

        sb.Append("## Sesiones\n\n");
        if (h.Sessions.Count == 0)
            sb.Append("_Sin sesiones todavía._\n\n");
        else
        {
            sb.Append("| Rol | Agente | Sesión | Inicio | Último uso | Retomar |\n|---|---|---|---|---|---|\n");
            foreach (var s in h.Sessions.OrderBy(s => s.Reviewer is not null).ThenBy(s => s.Reviewer, StringComparer.Ordinal))
            {
                sb.Append("| ").Append(s.Reviewer ?? "revisión")
                    .Append(" | ").Append(s.Agent).Append(s.IsDefault ? " ⭐" : "")
                    .Append(" | `").Append(s.AgentSessionId).Append('`')
                    .Append(" | ").Append(Display(s.StartedAt))
                    .Append(" | ").Append(Display(s.LastUsedAt))
                    .Append(" | `").Append(Cell(_resumeCommand(s.Agent, s.AgentSessionId))).Append("` |\n");
            }
            sb.Append('\n');
        }

        AppendPanel(sb, h);

        var latestSummary = h.Memos.LastOrDefault(m => m.Kind == MemoKinds.AgentSummary);
        var pending = latestSummary is null ? null : MemoText.Section(latestSummary.Body, "Pendientes");
        sb.Append("## Pendientes\n\n");
        sb.Append(pending is null
            ? "_Sin pendientes registrados._\n\n"
            : $"_Según el último resumen ({Display(latestSummary!.CreatedAt)})._\n\n{MemoText.NestHeadings(pending, 3)}\n\n");

        sb.Append("## Timeline\n\n");
        var entries = TimelineEntries(h);
        if (entries.Count == 0)
            sb.Append("_Sin actividad todavía._\n\n");
        foreach (var entry in entries)
        {
            sb.Append("### ").Append(entry.Title).Append("\n\n");
            if (entry.Round?.Prompt is { Length: > 0 } prompt)
            {
                sb.Append("> [!example]- Prompt enviado\n");
                AppendQuoted(sb, prompt.Trim());
                sb.Append('\n');
            }
            if (entry.Body is not null)
                sb.Append(entry.Body);
            foreach (var memo in entry.Memos)
                AppendMemo(sb, memo);
        }
        return sb.ToString();
    }

    private void AppendPanel(StringBuilder sb, PrHistory h)
    {
        sb.Append("## Panel de revisores\n\n");
        if (h.Runs.Count == 0)
        {
            sb.Append("_Sin análisis todavía. Corré `prt analyze ").Append(h.Pr.Key).Append("`._\n\n");
            return;
        }

        var latest = RunView.From(h.Runs[^1]);
        sb.Append("**").Append(latest.Run.Verdict ?? "SIN VEREDICTO").Append("** · ").Append(Display(latest.Run.StartedAt))
            .Append(" · [[").Append(RunLink(h.Pr, latest.Run)).Append("|Reporte completo]]\n\n");
        sb.Append(ReportRenderer.CountsLine(latest)).Append("\n\n");

        var open = latest.OpenFindings.Where(x => x.Finding.Severity >= Severity.Minor).ToList();
        if (open.Count > 0)
        {
            sb.Append("| | ID | Revisor | Hallazgo | Ubicación |\n|---|---|---|---|---|\n");
            foreach (var (reviewer, f) in open)
            {
                sb.Append("| ").Append(ReviewStandard.Icon(f.Severity)).Append(" | ").Append(f.Id).Append(" | ").Append(reviewer)
                    .Append(" | ").Append(Cell(f.Title)).Append(" | ").Append(f.Location.Length > 0 ? $"`{Cell(f.Location)}`" : "").Append(" |\n");
            }
            sb.Append('\n');
        }

        var questions = latest.Questions.ToList();
        if (questions.Count > 0)
        {
            sb.Append("**Preguntas para vos:**\n\n");
            foreach (var (reviewer, q) in questions)
                sb.Append("- [ ] **").Append(reviewer).Append(":** ").Append(q.ReplaceLineEndings(" ")).Append('\n');
            sb.Append('\n');
        }

        var failed = latest.Reviewers.Where(r => r.Report is null).Select(r => r.Result.Reviewer).ToList();
        if (failed.Count > 0)
            sb.Append("> [!warning] Sin reporte válido: ").Append(string.Join(", ", failed)).Append("\n\n");

        if (h.Runs.Count > 1)
        {
            sb.Append("Análisis anteriores: ");
            sb.Append(string.Join(" · ", h.Runs.Take(h.Runs.Count - 1).Reverse()
                .Select(r => $"[[{RunLink(h.Pr, r.Run)}|{Display(r.Run.StartedAt)} {r.Run.Verdict}]]")));
            sb.Append("\n\n");
        }
    }

    private sealed record TimelineEntry(DateTimeOffset At, string Title, Round? Round, IReadOnlyList<Memo> Memos, string? Body = null);

    private List<TimelineEntry> TimelineEntries(PrHistory h)
    {
        var sessions = h.Sessions.ToDictionary(s => s.Id);
        var entries = new List<TimelineEntry>();
        foreach (var round in h.Rounds)
        {
            var agent = sessions.TryGetValue(round.SessionId, out var s)
                ? s.Reviewer is null ? s.Agent : $"{s.Agent} ({s.Reviewer})"
                : "?";
            var title = new StringBuilder(Display(round.StartedAt)).Append(" — ").Append(agent)
                .Append(" · ").Append(round.Kind == RoundKinds.New ? "nueva sesión" : "sesión retomada");
            if (round.CommitAtStart is { Length: > 0 } commit)
                title.Append(" · `").Append(CommitHashes.Short(commit)).Append('`');
            entries.Add(new TimelineEntry(round.StartedAt, title.ToString(), round,
                h.Memos.Where(m => m.RoundId == round.Id).ToList()));
        }

        var roundIds = h.Rounds.Select(r => r.Id).ToHashSet();
        foreach (var memo in h.Memos.Where(m => m.RoundId is null || !roundIds.Contains(m.RoundId.Value)))
            entries.Add(new TimelineEntry(memo.CreatedAt, $"{Display(memo.CreatedAt)} — {MemoKinds.Label(memo.Kind)}", null, [memo]));

        foreach (var run in h.Runs)
        {
            var view = RunView.From(run);
            var body = $"{ReportRenderer.CountsLine(view)} · {view.Reviewers.Count} revisor(es) · [[{RunLink(h.Pr, run.Run)}|Reporte completo]]\n\n";
            entries.Add(new TimelineEntry(run.Run.StartedAt,
                $"{Display(run.Run.StartedAt)} — Panel de revisores · {run.Run.Verdict ?? "sin veredicto"}", null, [], body));
        }

        return entries.OrderBy(e => e.At).ToList();
    }

    private void AppendMemo(StringBuilder sb, Memo memo)
    {
        var callout = memo.Kind switch
        {
            MemoKinds.AgentSummary => "abstract",
            MemoKinds.Decision => "important",
            _ => "note",
        };
        sb.Append("> [!").Append(callout).Append("] ").Append(MemoKinds.Label(memo.Kind))
            .Append(" · ").Append(Local(memo.CreatedAt).ToString("HH:mm", CultureInfo.InvariantCulture)).Append('\n');
        AppendQuoted(sb, MemoText.NestHeadings(memo.Body, 4));
        sb.Append('\n');
    }

    private static void AppendQuoted(StringBuilder sb, string text)
    {
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
            sb.Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
    }

    // ---- Index ---------------------------------------------------------------------------

    public string RenderIndex(IReadOnlyList<PrHistory> histories)
    {
        var sb = new StringBuilder();
        sb.Append("---\ntags:\n  - pr-review-index\n---\n");
        sb.Append("# Revisiones de PRs\n\n");
        sb.Append("_Generado por prt; se sobrescribe en cada export._\n\n");

        foreach (var group in histories
                     .OrderByDescending(h => LastReview(h) ?? h.Pr.UpdatedAt)
                     .GroupBy(h => Or(h.Pr.State, "SIN ESTADO").ToUpperInvariant())
                     .OrderBy(g => g.Key == "OPEN" ? 0 : 1).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append("## ").Append(group.Key).Append("\n\n");
            sb.Append("| PR | Título | Autor | Veredicto | Abiertos | Última revisión | Rondas |\n|---|---|---|---|---|---|---|\n");
            foreach (var h in group)
            {
                var pr = h.Pr;
                var link = $"{_folder}/{SafeName(pr.Workspace)}/{SafeName(pr.Repo)}/{Path.GetFileNameWithoutExtension(NoteFileName(pr))}";
                sb.Append("| [[").Append(link).Append("\\|").Append(pr.Key).Append("]]")
                    .Append(" | ").Append(Cell(pr.Title))
                    .Append(" | ").Append(Cell(pr.Author))
                    .Append(" | ").Append(Cell(pr.Verdict ?? ""))
                    .Append(" | ").Append(h.Runs.Count == 0 ? "" : OpenSummary(RunView.From(h.Runs[^1])))
                    .Append(" | ").Append(LastReview(h) is { } last ? Display(last) : "")
                    .Append(" | ").Append(h.Rounds.Count).Append(" |\n");
            }
            sb.Append('\n');
        }

        sb.Append("## Vista Dataview\n\n");
        sb.Append("```dataview\nTABLE title, author, state, verdict, panel_verdict, blockers, majors, last_review\nFROM #pr-review\nSORT last_review DESC\n```\n");
        return sb.ToString();
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static string OpenSummary(RunView view) =>
        string.Join(" ", new[] { Severity.Blocker, Severity.Major, Severity.Minor }
            .Where(s => view.Count(s) > 0).Select(s => $"{ReviewStandard.Icon(s)}{view.Count(s)}"));

    private static DateTimeOffset? LastReview(PrHistory h)
    {
        var times = h.Rounds.Select(r => r.EndedAt ?? r.StartedAt).Concat(h.Memos.Select(m => m.CreatedAt))
            .Concat(h.Runs.Select(r => r.Run.EndedAt ?? r.Run.StartedAt)).ToList();
        return times.Count == 0 ? null : times.Max();
    }

    private static (string Before, string After) UserContent(string? previous)
    {
        if (previous is null)
            return ("", DefaultUserSection);

        var text = previous.ReplaceLineEndings("\n");
        if (text.StartsWith("---\n"))
        {
            var close = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (close >= 0)
                text = text[(close + 5)..];
        }

        var begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (begin >= 0 && end > begin)
            return (text[..begin], text[(end + EndMarker.Length)..]);

        // Markers were removed by hand: keep everything the note had, after the generated block.
        return ("", "\n\n" + text.Trim() + "\n");
    }

    private static string? FindExistingNote(string dir, int number)
    {
        if (!Directory.Exists(dir))
            return null;
        return Directory.EnumerateFiles(dir, $"PR-{number}*.md")
            .FirstOrDefault(f =>
            {
                var name = Path.GetFileName(f);
                return name == $"PR-{number}.md" || name.StartsWith($"PR-{number} - ", StringComparison.Ordinal);
            });
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".prt-tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex(@"[\\/:*?""<>|#^\[\]\p{Cc}]")]
    private static partial Regex UnsafeChars();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static string SafeName(string name) =>
        Spaces().Replace(UnsafeChars().Replace(name, " "), " ").Trim().Trim('.').Trim();

    private DateTimeOffset Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _timeZone);

    private string Display(DateTimeOffset value) =>
        Local(value).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static string Cell(string value) => value.ReplaceLineEndings(" ").Replace("|", "\\|");

    private static void Yaml(StringBuilder sb, string key, string value) =>
        sb.Append(key).Append(": ").Append(Quote(value)).Append('\n');

    private static void AppendList(StringBuilder sb, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            sb.Append(" []\n");
            return;
        }
        sb.Append('\n');
        foreach (var item in list)
            sb.Append("  - ").Append(Quote(item)).Append('\n');
    }

    private static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");
        return $"\"{escaped}\"";
    }
}
