using System.Text;
using Prt.Core.Domain;
using Prt.Core.Panel;
using static Prt.Web.Html;

namespace Prt.Web;

internal sealed partial class Pages
{
    /// <summary>Above this many diff lines, the remaining files are listed but not rendered.</summary>
    private const int MaxRenderedLines = 20_000;

    /// <summary>Files longer than this start collapsed.</summary>
    private const int CollapseFileLines = 1_500;

    /// <summary>The current diff of the PR (from Bitbucket), with the latest run's findings on it.</summary>
    public async Task<string> Diff(PrRef prRef, bool refresh)
    {
        var config = context.LoadConfig();
        PullRequest pr;
        RunView? latest;
        using (var store = context.OpenStore())
        {
            pr = store.FindPullRequest(prRef) ?? throw new ArgumentException($"{prRef.Key} no está registrado.");
            latest = store.ListRuns(pr.Id) is { Count: > 0 } runs ? RunView.From(runs[^1]) : null;
        }

        string diff;
        string source;
        try
        {
            var (text, fetchedAt) = await diffs.GetAsync(prRef, () => context.Bitbucket(config).GetDiffAsync(prRef), refresh);
            diff = text;
            source = $"Diff actual del PR en Bitbucket (obtenido {Date(fetchedAt)}) · <a href=\"{PrtWebServer.PrUrl(prRef)}/diff?refresh=1\">actualizar</a>";
        }
        catch (Exception ex) when (ex is Prt.Core.Bitbucket.BitbucketException or HttpRequestException or TaskCanceledException)
        {
            // Offline: fall back to the diff the latest analysis saved.
            var saved = latest is null ? null : UnifiedDiff.RunDiffPath(context.RunsRoot, prRef, latest.Run.Id);
            if (saved is null || !File.Exists(saved))
                throw new ArgumentException($"No se pudo obtener el diff de Bitbucket ({ex.Message}) y no hay uno guardado de un análisis.");
            diff = await File.ReadAllTextAsync(saved);
            source = $"<span class=\"v-changes\">No se pudo consultar Bitbucket ({E(ex.Message)}).</span> Se muestra el diff del último análisis ({Date(latest!.Run.StartedAt)}).";
        }

        var findings = latest is null ? [] : latest.OpenFindings.ToList();
        var title = latest is null
            ? ""
            : $" · hallazgos del <a href=\"{PrtWebServer.PrUrl(prRef)}/run/{latest.Run.Id}\">análisis del {Date(latest.Run.StartedAt)}</a>";
        return Page($"{pr.Key} · diff", RenderDiff(pr, diff, source + title, findings));
    }

    /// <summary>The diff a given run analysed, with that run's findings on it.</summary>
    public string RunDiff(PrRef prRef, long runId)
    {
        using var store = context.OpenStore();
        var pr = store.FindPullRequest(prRef) ?? throw new ArgumentException($"{prRef.Key} no está registrado.");
        var run = store.ListRuns(pr.Id).FirstOrDefault(r => r.Run.Id == runId)
                  ?? throw new ArgumentException($"No existe el análisis {runId} de {prRef.Key}.");
        var path = UnifiedDiff.RunDiffPath(context.RunsRoot, prRef, runId);
        if (!File.Exists(path))
            throw new ArgumentException("Ese análisis no tiene el diff guardado (no se pudo obtener de Bitbucket cuando corrió).");

        var view = RunView.From(run);
        var source = $"Diff analizado en el <a href=\"{PrtWebServer.PrUrl(prRef)}/run/{runId}\">análisis del {Date(view.Run.StartedAt)}</a>";
        if (view.Run.HeadCommit is { Length: > 0 } head)
            source += $" · head <code>{E(Prt.Core.Bitbucket.CommitHashes.Short(head))}</code>";
        source += $" · <a href=\"{PrtWebServer.PrUrl(prRef)}/diff\">ver el diff actual</a>";
        return Page($"{pr.Key} · diff del análisis", RenderDiff(pr, File.ReadAllText(path), source, view.OpenFindings.ToList()));
    }

    private static string RenderDiff(PullRequest pr, string diff, string source, IReadOnlyList<(string Reviewer, Finding Finding)> findings)
    {
        var files = UnifiedDiff.Parse(diff);
        var sb = new StringBuilder(diff.Length * 2 + 4096);
        sb.Append("<p><a href=\"").Append(PrtWebServer.PrUrl(pr.Ref)).Append("\">← ").Append(E(pr.Key)).Append(" · ").Append(E(pr.Title)).Append("</a></p>");
        sb.Append("<h1>Cambios · <code>").Append(E(pr.SourceBranch)).Append("</code> → <code>").Append(E(pr.DestinationBranch)).Append("</code></h1>");
        sb.Append("<p class=\"muted\">").Append(source).Append("</p>");

        if (files.Count == 0)
        {
            sb.Append("<p class=\"muted\">El diff está vacío.</p>");
            return sb.ToString();
        }

        // Summary with links to each file.
        var fileFindings = files.Select(f => findings.Where(x => UnifiedDiff.SamePath(x.Finding.File, f.Path)).ToList()).ToList();
        sb.Append("<p>").Append(files.Count).Append(" archivo(s) · <span class=\"add-n\">+").Append(files.Sum(f => f.Added))
            .Append("</span> <span class=\"del-n\">−").Append(files.Sum(f => f.Removed)).Append("</span>");
        if (findings.Count > 0)
            sb.Append(" · ").Append(findings.Count).Append(" hallazgo(s) abiertos");
        sb.Append("</p><table class=\"files\">");
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            sb.Append("<tr><td><a href=\"#file-").Append(i).Append("\">").Append(E(FileLabel(f))).Append("</a> ").Append(StatusBadge(f.Status))
                .Append("</td><td class=\"add-n\">+").Append(f.Added).Append("</td><td class=\"del-n\">−").Append(f.Removed).Append("</td><td>");
            foreach (var s in fileFindings[i].Select(x => x.Finding.Severity).Distinct().OrderDescending())
                sb.Append(SeverityBadge(s));
            sb.Append("</td></tr>");
        }
        sb.Append("</table>");

        var rendered = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var mine = fileFindings[i];
            var open = file.LineCount <= CollapseFileLines || mine.Count > 0;
            sb.Append("<details class=\"file\" id=\"file-").Append(i).Append('"').Append(open ? " open" : "").Append("><summary><code>")
                .Append(E(FileLabel(file))).Append("</code> ").Append(StatusBadge(file.Status))
                .Append(" <span class=\"add-n\">+").Append(file.Added).Append("</span> <span class=\"del-n\">−").Append(file.Removed).Append("</span></summary>");

            if (rendered > MaxRenderedLines)
            {
                sb.Append("<p class=\"muted\">Diff demasiado grande para mostrarlo completo: ")
                    .Append(pr.Url.Length > 0 ? $"<a href=\"{E(pr.Url)}/diff\" rel=\"noreferrer\">abrilo en Bitbucket</a>." : "abrilo en Bitbucket.")
                    .Append("</p></details>");
                continue;
            }

            // Findings whose line is not part of the diff are shown at the top of the file.
            var placed = new HashSet<Finding>();
            var newLines = file.Hunks.SelectMany(h => h.Lines).Where(l => l.NewLine is not null).Select(l => l.NewLine!.Value).ToHashSet();
            var loose = mine.Where(x => x.Finding.Line is not { } line || !newLines.Contains(line)).ToList();
            foreach (var (reviewer, finding) in loose)
            {
                sb.Append(FindingNote(reviewer, finding));
                placed.Add(finding);
            }

            if (file.Status == DiffFileStatus.Binary)
                sb.Append("<p class=\"muted\">Archivo binario.</p>");

            sb.Append("<table class=\"diff\"><colgroup><col class=\"ln\"><col class=\"ln\"><col></colgroup>");
            foreach (var hunk in file.Hunks)
            {
                sb.Append("<tr class=\"hunk\"><td></td><td></td><td>").Append(E(hunk.Header)).Append("</td></tr>");
                foreach (var line in hunk.Lines)
                {
                    rendered++;
                    var (css, sign) = line.Kind switch
                    {
                        DiffLineKind.Added => ("add", '+'),
                        DiffLineKind.Removed => ("del", '−'),
                        DiffLineKind.Note => ("note", ' '),
                        _ => ("ctx", ' '),
                    };
                    sb.Append("<tr class=\"").Append(css).Append('"');
                    if (line.NewLine is { } anchor)
                        sb.Append(" id=\"file-").Append(i).Append("-L").Append(anchor).Append('"');
                    sb.Append("><td class=\"ln\">").Append(line.OldLine).Append("</td><td class=\"ln\">").Append(line.NewLine)
                        .Append("</td><td><span class=\"sign\">").Append(sign).Append("</span>").Append(E(line.Text)).Append("</td></tr>");

                    if (line.NewLine is { } n)
                    {
                        foreach (var (reviewer, finding) in mine.Where(x => x.Finding.Line == n && !placed.Contains(x.Finding)))
                        {
                            sb.Append("<tr class=\"finding\"><td></td><td></td><td>").Append(FindingNote(reviewer, finding)).Append("</td></tr>");
                            placed.Add(finding);
                        }
                    }
                }
            }
            sb.Append("</table></details>");
        }
        return sb.ToString();
    }

    private static string FindingNote(string reviewer, Finding f) =>
        $"<div class=\"finding-note\">{SeverityBadge(f.Severity)}<b>{E(f.Id)}</b> {E(f.Title)} <span class=\"muted\">· {E(reviewer)}" +
        (f.Line is { } line ? $" · línea {line}" : "") + "</span>" +
        (f.Recommendation.Length > 0 ? $"<div class=\"muted\">{E(f.Recommendation)}</div>" : "") + "</div>";

    private static string FileLabel(DiffFile f) =>
        f.Status == DiffFileStatus.Renamed ? $"{f.OldPath} → {f.NewPath}" : f.Path;

    private static string StatusBadge(DiffFileStatus status) => status switch
    {
        DiffFileStatus.Added => "<span class=\"tag\">nuevo</span>",
        DiffFileStatus.Deleted => "<span class=\"tag\">eliminado</span>",
        DiffFileStatus.Renamed => "<span class=\"tag\">renombrado</span>",
        DiffFileStatus.Binary => "<span class=\"tag\">binario</span>",
        _ => "",
    };
}
