using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;
using Prt.Core.Export;

namespace Prt.Core.Panel;

/// <summary>A run with its reports decoded, ready to display.</summary>
public sealed record RunView(ReviewRun Run, IReadOnlyList<(ReviewerResult Result, ReviewerReport? Report)> Reviewers)
{
    public static RunView From(RunHistory history) => new(
        history.Run,
        history.Results.Select(r => (r, r.ReportJson is { } json ? TryDecode(json) : null)).ToList());

    public IEnumerable<ReviewerReport> Reports => Reviewers.Select(r => r.Report).OfType<ReviewerReport>();

    public IEnumerable<(string Reviewer, Finding Finding)> OpenFindings =>
        Reports.SelectMany(r => r.OpenFindings.Select(f => (r.Reviewer, f)))
            .OrderByDescending(x => x.f.Severity).ThenBy(x => x.Reviewer, StringComparer.Ordinal);

    public IEnumerable<(string Reviewer, Finding Finding)> ResolvedFindings =>
        Reports.SelectMany(r => r.Findings.Where(f => !f.IsOpen).Select(f => (r.Reviewer, f)));

    public IEnumerable<(string Reviewer, string Question)> Questions =>
        Reports.SelectMany(r => r.Questions.Select(q => (r.Reviewer, q)));

    public int Count(Severity severity) => OpenFindings.Count(x => x.Finding.Severity == severity);

    private static ReviewerReport? TryDecode(string json)
    {
        try
        {
            return ReviewerReport.FromJson(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>Markdown rendering of panel runs. Every run reads the same, whatever agents produced it.</summary>
public static partial class ReportRenderer
{
    private static readonly Severity[] Severities = [Severity.Blocker, Severity.Major, Severity.Minor, Severity.Info];

    public static string CountsLine(RunView view) =>
        string.Join(" · ", Severities.Select(s => $"{ReviewStandard.Icon(s)} {view.Count(s)} {ReviewStandard.Label(s).ToLowerInvariant()}"));

    /// <summary>
    /// Full report of a run, with headings starting at <paramref name="level"/>. With <paramref name="links"/>
    /// (Obsidian), the ids of the findings table link to their detail.
    /// </summary>
    public static string Render(RunView view, Func<DateTimeOffset, string> formatDate, int level = 2, bool links = false)
    {
        var h = new string('#', level);
        var sb = new StringBuilder();
        var run = view.Run;

        sb.Append(h).Append(" Resultado: **").Append(run.Verdict ?? "SIN VEREDICTO").Append("**\n\n");
        sb.Append('_').Append(formatDate(run.StartedAt));
        if (run.HeadCommit is { Length: > 0 } head)
            sb.Append(" · head `").Append(CommitHashes.Short(head)).Append('`');
        sb.Append(" · ").Append(view.Reviewers.Count).Append(" revisor(es)_\n\n");
        sb.Append(CountsLine(view)).Append("\n\n");

        sb.Append("| Revisor | Agente | Veredicto | 🔴 | 🟠 | 🟡 | 🔵 | Resueltos |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var (result, report) in view.Reviewers)
        {
            sb.Append("| ").Append(MarkdownText.Cell(result.Reviewer)).Append(" | ").Append(MarkdownText.Cell(result.Agent)).Append(" | ");
            if (report is null)
            {
                sb.Append(result.Status == ResultStatus.Invalid ? "⚠️ formato inválido" : "⚠️ falló").Append(" | – | – | – | – | – |\n");
                continue;
            }
            sb.Append(ReviewStandard.Label(report.Verdict));
            foreach (var severity in Severities)
                sb.Append(" | ").Append(report.OpenFindings.Count(f => f.Severity == severity));
            sb.Append(" | ").Append(report.Findings.Count(f => !f.IsOpen)).Append(" |\n");
        }
        sb.Append('\n');

        var open = view.OpenFindings.ToList();
        sb.Append(h).Append(" Hallazgos abiertos\n\n");
        if (open.Count == 0)
            sb.Append("_Sin hallazgos abiertos._\n\n");
        else
        {
            sb.Append(FindingsTable(open.Select(x => x.Finding), links ? "" : null, withStatus: true)).Append('\n');

            sb.Append(h).Append(" Detalle\n\n");
            foreach (var (reviewer, f) in open)
                AppendFinding(sb, h + "#", reviewer, f, links);
        }

        var resolved = view.ResolvedFindings.ToList();
        if (resolved.Count > 0)
        {
            sb.Append(h).Append(" Resueltos desde la pasada anterior\n\n");
            foreach (var (reviewer, f) in resolved)
                sb.Append("- ✅ ").Append(f.Id).Append(" · ").Append(MarkdownText.Prose(f.Title)).Append(" (").Append(reviewer).Append(")\n");
            sb.Append('\n');
        }

        var questions = view.Questions.ToList();
        if (questions.Count > 0)
        {
            sb.Append(h).Append(" Preguntas para el revisor humano\n\n");
            foreach (var (reviewer, q) in questions)
                sb.Append("- [ ] **").Append(reviewer).Append(":** ").Append(MarkdownText.Prose(q.ReplaceLineEndings(" "))).Append('\n');
            sb.Append('\n');
        }

        sb.Append(h).Append(" Resúmenes por revisor\n\n");
        foreach (var (result, report) in view.Reviewers)
        {
            sb.Append(h).Append("# ").Append(result.Reviewer).Append(" · ").Append(result.Agent).Append("\n\n");
            if (report is not null)
                sb.Append(report.Summary.Length > 0 ? MarkdownText.Prose(report.Summary) : "_Sin resumen._").Append("\n\n");
            else
                sb.Append("> [!warning] ").Append(result.Status == ResultStatus.Invalid ? "Respuesta fuera de formato" : "Falló")
                    .Append("\n> ").Append(MarkdownText.Prose((result.Error ?? "").ReplaceLineEndings(" "))).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Findings as <c>| ID | Sev. | Hallazgo | Ubicación | Acción |</c> (plus <c>Estado</c>). With <paramref name="linkTarget"/>,
    /// each id links to its detail block in that note ("" for the same note). The reviewer is implied by the id prefix.
    /// </summary>
    public static string FindingsTable(IEnumerable<Finding> findings, string? linkTarget, bool withStatus)
    {
        var sb = new StringBuilder(withStatus
            ? "| ID | Sev. | Hallazgo | Ubicación | Acción | Estado |\n|---|---|---|---|---|---|\n"
            : "| ID | Sev. | Hallazgo | Ubicación | Acción |\n|---|---|---|---|---|\n");
        foreach (var f in findings)
        {
            sb.Append("| ").Append(linkTarget is null ? f.Id : $"[[{linkTarget}#^{BlockId(f)}\\|{f.Id}]]")
                .Append(" | ").Append(ReviewStandard.Icon(f.Severity))
                .Append(" | ").Append(MarkdownText.Cell(f.Title))
                .Append(" | ").Append(f.File is null ? "" : MarkdownText.CodeCell(ShortLocation(f.File, f.Line)))
                .Append(" | ").Append(MarkdownText.Cell(Action(f.Recommendation)));
            if (withStatus)
                sb.Append(" | ").Append(ReviewStandard.Label(f.Status));
            sb.Append(" |\n");
        }
        return sb.ToString();
    }

    /// <summary>Obsidian block id of a finding's detail (<c>ARQ-1</c> → <c>arq-1</c>).</summary>
    public static string BlockId(Finding f) => NonBlockIdChars().Replace(f.Id.ToLowerInvariant(), "-");

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex NonBlockIdChars();

    // File name and line: the full path is in the detail.
    private static string ShortLocation(string file, int? line) =>
        line is { } n ? $"{Path.GetFileName(file)}:{n}" : Path.GetFileName(file);

    [GeneratedRegex(@"^.+?[.;](?=\s|$)")]
    private static partial Regex FirstSentence();

    // First sentence of the recommendation, short enough for a table.
    private static string Action(string recommendation)
    {
        var line = recommendation.ReplaceLineEndings(" ").Trim();
        var sentence = FirstSentence().Match(line) is { Success: true } m ? m.Value.TrimEnd('.', ';') : line;
        return MarkdownText.Truncate(sentence, 60);
    }

    private static void AppendFinding(StringBuilder sb, string heading, string reviewer, Finding f, bool links)
    {
        sb.Append(heading).Append(' ').Append(ReviewStandard.Icon(f.Severity)).Append(' ').Append(f.Id).Append(" · ")
            .Append(MarkdownText.Prose(f.Title)).Append("\n\n");
        sb.Append("**Severidad:** ").Append(ReviewStandard.Label(f.Severity))
            .Append(" · **Revisor:** ").Append(reviewer);
        if (f.Category.Length > 0)
            sb.Append(" · **Categoría:** ").Append(MarkdownText.Prose(f.Category.ReplaceLineEndings(" ")));
        if (f.Location.Length > 0)
            sb.Append(" · **Ubicación:** ").Append(MarkdownText.Code(f.Location));
        sb.Append(" · **Confianza:** ").Append(ReviewStandard.Label(f.Confidence))
            .Append(" · **Estado:** ").Append(ReviewStandard.Label(f.Status));
        if (links)
            sb.Append(" ^").Append(BlockId(f));
        sb.Append("\n\n");
        if (f.Description.Length > 0)
            sb.Append(MarkdownText.Prose(MemoText.NestHeadings(f.Description, 6))).Append("\n\n");
        if (f.Recommendation.Length > 0)
            sb.Append("**Recomendación:** ").Append(MarkdownText.Prose(f.Recommendation)).Append("\n\n");
    }

    public static string DefaultDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
