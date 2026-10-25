using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;

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
        return path;
    }

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
                sb.Append("    resume: ").Append(Quote(_resumeCommand(s.Agent, s.AgentSessionId))).Append('\n');
            }
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
        sb.Append("Retomar la revisión: `prt review ").Append(pr.Key).Append("`\n\n");

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
            sb.Append("| Agente | Sesión | Inicio | Último uso | Retomar |\n|---|---|---|---|---|\n");
            foreach (var s in h.Sessions)
            {
                sb.Append("| ").Append(s.Agent).Append(s.IsDefault ? " ⭐" : "")
                    .Append(" | `").Append(s.AgentSessionId).Append('`')
                    .Append(" | ").Append(Display(s.StartedAt))
                    .Append(" | ").Append(Display(s.LastUsedAt))
                    .Append(" | `").Append(Cell(_resumeCommand(s.Agent, s.AgentSessionId))).Append("` |\n");
            }
            sb.Append('\n');
        }

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
            foreach (var memo in entry.Memos)
                AppendMemo(sb, memo);
        }
        return sb.ToString();
    }

    private sealed record TimelineEntry(DateTimeOffset At, string Title, Round? Round, IReadOnlyList<Memo> Memos);

    private List<TimelineEntry> TimelineEntries(PrHistory h)
    {
        var sessions = h.Sessions.ToDictionary(s => s.Id);
        var entries = new List<TimelineEntry>();
        foreach (var round in h.Rounds)
        {
            var agent = sessions.TryGetValue(round.SessionId, out var s) ? s.Agent : "?";
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
            sb.Append("| PR | Título | Autor | Veredicto | Última revisión | Rondas |\n|---|---|---|---|---|---|\n");
            foreach (var h in group)
            {
                var pr = h.Pr;
                var link = $"{_folder}/{SafeName(pr.Workspace)}/{SafeName(pr.Repo)}/{Path.GetFileNameWithoutExtension(NoteFileName(pr))}";
                sb.Append("| [[").Append(link).Append("\\|").Append(pr.Key).Append("]]")
                    .Append(" | ").Append(Cell(pr.Title))
                    .Append(" | ").Append(Cell(pr.Author))
                    .Append(" | ").Append(Cell(pr.Verdict ?? ""))
                    .Append(" | ").Append(LastReview(h) is { } last ? Display(last) : "")
                    .Append(" | ").Append(h.Rounds.Count).Append(" |\n");
            }
            sb.Append('\n');
        }

        sb.Append("## Vista Dataview\n\n");
        sb.Append("```dataview\nTABLE title, author, state, verdict, last_review, rounds\nFROM #pr-review\nSORT last_review DESC\n```\n");
        return sb.ToString();
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static DateTimeOffset? LastReview(PrHistory h)
    {
        var times = h.Rounds.Select(r => r.EndedAt ?? r.StartedAt).Concat(h.Memos.Select(m => m.CreatedAt)).ToList();
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
