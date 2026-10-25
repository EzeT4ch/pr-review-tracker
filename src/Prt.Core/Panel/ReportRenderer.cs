using System.Globalization;
using System.Text;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;

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
public static class ReportRenderer
{
    private static readonly Severity[] Severities = [Severity.Blocker, Severity.Major, Severity.Minor, Severity.Info];

    public static string CountsLine(RunView view) =>
        string.Join(" · ", Severities.Select(s => $"{ReviewStandard.Icon(s)} {view.Count(s)} {ReviewStandard.Label(s).ToLowerInvariant()}"));

    /// <summary>Full report of a run, with headings starting at <paramref name="level"/>.</summary>
    public static string Render(RunView view, Func<DateTimeOffset, string> formatDate, int level = 2)
    {
        var h = new string('#', level);
        var sb = new StringBuilder();
        var run = view.Run;

        sb.Append(h).Append(" Resultado: **").Append(run.Verdict ?? "SIN VEREDICTO").Append("**\n\n");
        sb.Append('_').Append(formatDate(run.StartedAt));
        if (run.HeadCommit is { Length: > 0 } head)
            sb.Append(" · head `").Append(CommitHashes.Short(head)).Append('`');
        if (run.BaseBranch is { Length: > 0 } baseBranch)
            sb.Append(" · base `").Append(baseBranch).Append("` @ `").Append(CommitHashes.Short(run.BaseCommit)).Append("` (clon gestionado)");
        sb.Append(" · ").Append(view.Reviewers.Count).Append(" revisor(es)_\n\n");
        sb.Append(CountsLine(view)).Append("\n\n");

        sb.Append("| Revisor | Agente | Veredicto | 🔴 | 🟠 | 🟡 | 🔵 | Resueltos |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var (result, report) in view.Reviewers)
        {
            sb.Append("| ").Append(result.Reviewer).Append(" | ").Append(result.Agent).Append(" | ");
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
            sb.Append("| | ID | Revisor | Hallazgo | Ubicación | Estado |\n|---|---|---|---|---|---|\n");
            foreach (var (reviewer, f) in open)
            {
                sb.Append("| ").Append(ReviewStandard.Icon(f.Severity)).Append(" | ").Append(f.Id).Append(" | ").Append(reviewer)
                    .Append(" | ").Append(Cell(f.Title)).Append(" | ").Append(f.Location.Length > 0 ? $"`{Cell(f.Location)}`" : "")
                    .Append(" | ").Append(ReviewStandard.Label(f.Status)).Append(" |\n");
            }
            sb.Append('\n');

            sb.Append(h).Append(" Detalle\n\n");
            foreach (var (reviewer, f) in open)
                AppendFinding(sb, h + "#", reviewer, f);
        }

        var resolved = view.ResolvedFindings.ToList();
        if (resolved.Count > 0)
        {
            sb.Append(h).Append(" Resueltos desde la pasada anterior\n\n");
            foreach (var (reviewer, f) in resolved)
                sb.Append("- ✅ ").Append(f.Id).Append(" · ").Append(f.Title).Append(" (").Append(reviewer).Append(")\n");
            sb.Append('\n');
        }

        var questions = view.Questions.ToList();
        if (questions.Count > 0)
        {
            sb.Append(h).Append(" Preguntas para el revisor humano\n\n");
            foreach (var (reviewer, q) in questions)
                sb.Append("- [ ] **").Append(reviewer).Append(":** ").Append(q.ReplaceLineEndings(" ")).Append('\n');
            sb.Append('\n');
        }

        sb.Append(h).Append(" Resúmenes por revisor\n\n");
        foreach (var (result, report) in view.Reviewers)
        {
            sb.Append(h).Append("# ").Append(result.Reviewer).Append(" · ").Append(result.Agent).Append("\n\n");
            if (report is not null)
                sb.Append(report.Summary.Length > 0 ? report.Summary : "_Sin resumen._").Append("\n\n");
            else
                sb.Append("> [!warning] ").Append(result.Status == ResultStatus.Invalid ? "Respuesta fuera de formato" : "Falló")
                    .Append("\n> ").Append((result.Error ?? "").ReplaceLineEndings(" ")).Append("\n\n");
        }
        return sb.ToString();
    }

    private static void AppendFinding(StringBuilder sb, string heading, string reviewer, Finding f)
    {
        sb.Append(heading).Append(' ').Append(ReviewStandard.Icon(f.Severity)).Append(' ').Append(f.Id).Append(" · ").Append(f.Title).Append("\n\n");
        sb.Append("**Severidad:** ").Append(ReviewStandard.Label(f.Severity))
            .Append(" · **Revisor:** ").Append(reviewer);
        if (f.Category.Length > 0)
            sb.Append(" · **Categoría:** ").Append(f.Category);
        if (f.Location.Length > 0)
            sb.Append(" · **Ubicación:** `").Append(f.Location).Append('`');
        sb.Append(" · **Confianza:** ").Append(ReviewStandard.Label(f.Confidence))
            .Append(" · **Estado:** ").Append(ReviewStandard.Label(f.Status)).Append("\n\n");
        if (f.Description.Length > 0)
            sb.Append(Export.MemoText.NestHeadings(f.Description, 6)).Append("\n\n");
        if (f.Recommendation.Length > 0)
            sb.Append("**Recomendación:** ").Append(f.Recommendation).Append("\n\n");
    }

    private static string Cell(string value) => value.ReplaceLineEndings(" ").Replace("|", "\\|");

    public static string DefaultDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
