using System.Globalization;
using System.Text;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;
using Prt.Core.Panel;
using Prt.Core.Review;
using static Prt.Web.Html;

namespace Prt.Web;

/// <summary>
/// What the diff page was asked to open: the line to comment (<c>c</c>, "{file}-{n|o}-{line}" or "{file}-f" for the whole file),
/// a finding to publish (<c>f</c>), a thread to reply to (<c>reply</c>), and after a failed post its text and error.
/// </summary>
internal sealed record CommentRequest(string? Target = null, string? FindingId = null, long? ReplyTo = null, string? Draft = null, string? Error = null);

internal sealed partial class Pages
{
    /// <summary>Above this many diff lines, the remaining files are listed but not rendered.</summary>
    private const int MaxRenderedLines = 20_000;

    /// <summary>Files longer than this start collapsed.</summary>
    private const int CollapseFileLines = 1_500;

    /// <summary>Comments on the diff page: the ones in Bitbucket (null when they could not be read) and the ones published from prt.</summary>
    private sealed record CommentView(
        string DiffUrl,
        IReadOnlyList<PrComment>? Remote,
        string? RemoteError,
        IReadOnlyList<PrLineComment> Local,
        CommentRequest Request);

    /// <summary>The current diff of the PR (from Bitbucket), with the latest run's findings and the PR's comments on it.</summary>
    public async Task<string> Diff(PrRef prRef, bool refresh, CommentRequest? request = null)
    {
        var config = context.LoadConfig();
        PullRequest pr;
        RunView? latest;
        IReadOnlyList<PrLineComment> local;
        using (var store = context.OpenStore())
        {
            pr = store.FindPullRequest(prRef) ?? throw new ArgumentException($"{prRef.Key} no está registrado.");
            latest = store.ListRuns(pr.Id) is { Count: > 0 } runs ? RunView.From(runs[^1]) : null;
            local = store.ListLineComments(pr.Id);
        }

        string diff;
        string source;
        var online = true;
        try
        {
            var (text, fetchedAt) = await diffs.GetAsync(prRef, () => context.Bitbucket(config).GetDiffAsync(prRef), refresh);
            diff = text;
            source = $"Diff actual del PR en Bitbucket (obtenido {Date(fetchedAt)}) · <a href=\"{PrtWebServer.PrUrl(prRef)}/diff?refresh=1\">actualizar</a>";
        }
        catch (Exception ex) when (ex is BitbucketException or HttpRequestException or TaskCanceledException)
        {
            // Offline: fall back to the diff the latest analysis saved.
            var saved = latest is null ? null : UnifiedDiff.RunDiffPath(context.RunsRoot, prRef, latest.Run.Id);
            if (saved is null || !File.Exists(saved))
                throw new ArgumentException($"No se pudo obtener el diff de Bitbucket ({ex.Message}) y no hay uno guardado de un análisis.");
            diff = await File.ReadAllTextAsync(saved);
            source = $"<span class=\"v-changes\">No se pudo consultar Bitbucket ({E(ex.Message)}).</span> Se muestra el diff del último análisis ({Date(latest!.Run.StartedAt)}).";
            online = false;
        }

        IReadOnlyList<PrComment>? remote = null;
        string? remoteError = online ? null : "sin conexión con Bitbucket";
        if (online)
        {
            try
            {
                remote = (await comments.GetAsync(prRef, () => context.Bitbucket(config).GetCommentsAsync(prRef), refresh)).Value;
            }
            catch (Exception ex) when (ex is BitbucketException or HttpRequestException or TaskCanceledException)
            {
                remoteError = ex.Message;
            }
        }

        var findings = latest is null ? [] : latest.OpenFindings.ToList();
        var title = latest is null
            ? ""
            : $" · hallazgos del <a href=\"{PrtWebServer.PrUrl(prRef)}/run/{latest.Run.Id}\">análisis del {Date(latest.Run.StartedAt)}</a>";
        var view = new CommentView(PrtWebServer.PrUrl(prRef) + "/diff", remote, remoteError, local, request ?? new CommentRequest());
        return Page($"{pr.Key} · diff", RenderDiff(pr, diff, source + title, findings, view));
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
            source += $" · head <code>{E(CommitHashes.Short(head))}</code>";
        source += $" · <a href=\"{PrtWebServer.PrUrl(prRef)}/diff\">ver el diff actual</a> (para comentar)";
        return Page($"{pr.Key} · diff del análisis", RenderDiff(pr, File.ReadAllText(path), source, view.OpenFindings.ToList(), null));
    }

    /// <summary>Anchor of a diff row: <c>L</c> for a line of the new file, <c>O</c> for a removed line of the old one.</summary>
    internal static string LineAnchor(int file, bool oldSide, int line) =>
        $"file-{file}-{(oldSide ? 'O' : 'L')}{line.ToString(CultureInfo.InvariantCulture)}";

    private string RenderDiff(PullRequest pr, string diff, string source, IReadOnlyList<(string Reviewer, Finding Finding)> findings, CommentView? cv)
    {
        var files = UnifiedDiff.Parse(diff);
        var sb = new StringBuilder(diff.Length * 2 + 4096);
        sb.Append("<p><a href=\"").Append(PrtWebServer.PrUrl(pr.Ref)).Append("\">← ").Append(E(pr.Key)).Append(" · ").Append(E(pr.Title)).Append("</a></p>");
        sb.Append("<h1>Cambios · <code>").Append(E(pr.SourceBranch)).Append("</code> → <code>").Append(E(pr.DestinationBranch)).Append("</code></h1>");
        sb.Append("<p class=\"muted\">").Append(source).Append("</p>");

        if (cv?.Request.Error is { } error)
            sb.Append("<div class=\"job job-failed\">No se pudo publicar el comentario: ").Append(E(error)).Append("</div>");
        if (cv is { Remote: null })
            sb.Append("<p class=\"muted\">Comentarios de Bitbucket no disponibles (").Append(E(cv.RemoteError)).Append("): no se pueden ver ni agregar.</p>");

        if (files.Count == 0)
        {
            sb.Append("<p class=\"muted\">El diff está vacío.</p>");
            return sb.ToString();
        }

        var remote = cv?.Remote ?? [];
        var replies = remote.Where(c => c.ParentId is not null).ToLookup(c => c.ParentId!.Value);
        var remoteIds = remote.Select(c => c.Id).ToHashSet();
        var roots = remote.Where(c => c.ParentId is not { } p || !remoteIds.Contains(p)).ToList();
        var published = (cv?.Local ?? []).Where(c => c.FindingId is not null).GroupBy(c => c.FindingId!).ToDictionary(g => g.Key, g => g.Last());

        // Where a finding to publish (?f=) goes: its line if the diff has it, else its file.
        var request = cv?.Request ?? new CommentRequest();
        (string Reviewer, Finding Finding)? toPublish = request.FindingId is { } fid ? findings.FirstOrDefault(x => x.Finding.Id == fid) : null;
        if (toPublish is { Finding: var pf } && request.Target is null)
        {
            var index = files.Select((f, n) => (f, n)).FirstOrDefault(x => UnifiedDiff.SamePath(pf.File, x.f.Path), (null!, -1)).n;
            if (index >= 0)
            {
                var inDiff = pf.Line is { } l && files[index].Hunks.SelectMany(h => h.Lines).Any(x => x.NewLine == l);
                request = request with { Target = inDiff ? $"{index}-n-{pf.Line}" : $"{index}-f" };
            }
        }
        var draft = request.Draft ?? (toPublish is { } tp ? CommentService.FindingComment(tp.Reviewer, tp.Finding) : null);

        // Summary with links to each file.
        var fileFindings = files.Select(f => findings.Where(x => UnifiedDiff.SamePath(x.Finding.File, f.Path)).ToList()).ToList();
        var fileComments = files.Select(f => roots.Where(c => c.IsInline && (UnifiedDiff.SamePath(c.Path, f.Path) || UnifiedDiff.SamePath(c.Path, f.OldPath))).ToList()).ToList();
        sb.Append("<p>").Append(files.Count).Append(" archivo(s) · <span class=\"add-n\">+").Append(files.Sum(f => f.Added))
            .Append("</span> <span class=\"del-n\">−").Append(files.Sum(f => f.Removed)).Append("</span>");
        if (findings.Count > 0)
            sb.Append(" · ").Append(findings.Count).Append(" hallazgo(s) abiertos");
        if (remote.Count > 0)
            sb.Append(" · ").Append(remote.Count).Append(" comentario(s) en Bitbucket");
        sb.Append("</p>");
        if (cv is { Remote: not null })
            sb.Append("<p class=\"muted\">Pasá el mouse por una línea y tocá <b>+</b> para comentarla en Bitbucket.</p>");

        var general = roots.Where(c => !c.IsInline).ToList();
        if (general.Count > 0)
        {
            sb.Append("<details><summary>").Append(general.Count).Append(" comentario(s) generales del PR</summary>");
            foreach (var root in general)
                sb.Append(Thread(root, replies, cv!, request.ReplyTo, draft, pr));
            sb.Append("</details>");
        }

        sb.Append("<table class=\"files\">");
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            sb.Append("<tr><td><a href=\"#file-").Append(i).Append("\">").Append(E(FileLabel(f))).Append("</a> ").Append(StatusBadge(f.Status))
                .Append("</td><td class=\"add-n\">+").Append(f.Added).Append("</td><td class=\"del-n\">−").Append(f.Removed).Append("</td><td>");
            foreach (var s in fileFindings[i].Select(x => x.Finding.Severity).Distinct().OrderDescending())
                sb.Append(SeverityBadge(s));
            if (fileComments[i].Count > 0)
                sb.Append(" <span class=\"tag\">💬 ").Append(fileComments[i].Count).Append("</span>");
            sb.Append("</td></tr>");
        }
        sb.Append("</table>");

        var rendered = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var mine = fileFindings[i];
            var targetHere = request.Target is { } t && t.StartsWith($"{i}-", StringComparison.Ordinal);
            var open = file.LineCount <= CollapseFileLines || mine.Count > 0 || fileComments[i].Count > 0 || targetHere;
            sb.Append("<details class=\"file\" id=\"file-").Append(i).Append('"').Append(open ? " open" : "").Append("><summary><code>")
                .Append(E(FileLabel(file))).Append("</code> ").Append(StatusBadge(file.Status))
                .Append(" <span class=\"add-n\">+").Append(file.Added).Append("</span> <span class=\"del-n\">−").Append(file.Removed).Append("</span>");
            if (cv is { Remote: not null })
                sb.Append(" · <a href=\"").Append(cv.DiffUrl).Append("?c=").Append(i).Append("-f#c\">comentar el archivo</a>");
            sb.Append("</summary>");

            if (rendered > MaxRenderedLines)
            {
                sb.Append("<p class=\"muted\">Diff demasiado grande para mostrarlo completo: ")
                    .Append(pr.Url.Length > 0 ? $"<a href=\"{E(pr.Url)}/diff\" rel=\"noreferrer\">abrilo en Bitbucket</a>." : "abrilo en Bitbucket.")
                    .Append("</p></details>");
                continue;
            }

            var lines = file.Hunks.SelectMany(h => h.Lines).ToList();
            var newLines = lines.Where(l => l.NewLine is not null).Select(l => l.NewLine!.Value).ToHashSet();
            var oldOnly = lines.Where(l => l.NewLine is null && l.OldLine is not null).Select(l => l.OldLine!.Value).ToHashSet();

            // Comments on a line of the diff go under it; the rest (outdated, or on the whole file) at the top.
            var byNew = new Dictionary<int, List<PrComment>>();
            var byOld = new Dictionary<int, List<PrComment>>();
            var looseComments = new List<PrComment>();
            foreach (var c in fileComments[i])
            {
                if (c.To is { } to && newLines.Contains(to))
                    (byNew.TryGetValue(to, out var list) ? list : byNew[to] = []).Add(c);
                else if (c.To is null && c.From is { } from && oldOnly.Contains(from))
                    (byOld.TryGetValue(from, out var list) ? list : byOld[from] = []).Add(c);
                else
                    looseComments.Add(c);
            }

            // Findings whose line is not part of the diff are shown at the top of the file.
            var placed = new HashSet<Finding>();
            var loose = mine.Where(x => x.Finding.Line is not { } line || !newLines.Contains(line)).ToList();
            foreach (var (reviewer, finding) in loose)
            {
                sb.Append(FindingNote(reviewer, finding, cv, published));
                placed.Add(finding);
            }
            foreach (var c in looseComments)
            {
                var where = c.To is { } to ? $"línea {to}" : c.From is { } from ? $"línea {from} anterior" : "archivo";
                sb.Append("<div class=\"muted\">Comentario sobre ").Append(where)
                    .Append(c.To is null && c.From is null ? "" : " (fuera del diff actual)").Append(":</div>")
                    .Append(Thread(c, replies, cv!, request.ReplyTo, draft, pr));
            }
            if (request.Target == $"{i}-f" && cv is { Remote: not null })
                sb.Append(CommentForm(cv, pr, i, file.Path, null, false, null, request.FindingId, draft));

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
                    // Comments go on the new side, or on the old one for removed lines.
                    var oldSide = line.NewLine is null;
                    int? at = line.Kind == DiffLineKind.Note ? null : line.NewLine ?? line.OldLine;
                    sb.Append("<tr class=\"").Append(css).Append('"');
                    if (at is { } anchor)
                        sb.Append(" id=\"").Append(LineAnchor(i, oldSide, anchor)).Append('"');
                    sb.Append("><td class=\"ln\">");
                    if (oldSide && at is { } o && cv is { Remote: not null })
                        sb.Append(AddLink(cv, i, true, o));
                    sb.Append(line.OldLine).Append("</td><td class=\"ln\">");
                    if (!oldSide && at is { } n2 && cv is { Remote: not null })
                        sb.Append(AddLink(cv, i, false, n2));
                    sb.Append(line.NewLine).Append("</td><td><span class=\"sign\">").Append(sign).Append("</span>").Append(E(line.Text)).Append("</td></tr>");

                    if (line.NewLine is { } n)
                    {
                        foreach (var (reviewer, finding) in mine.Where(x => x.Finding.Line == n && !placed.Contains(x.Finding)))
                        {
                            sb.Append("<tr class=\"finding\"><td></td><td></td><td>").Append(FindingNote(reviewer, finding, cv, published)).Append("</td></tr>");
                            placed.Add(finding);
                        }
                    }

                    if (at is not { } l2)
                        continue;
                    var threads = (oldSide ? byOld : byNew).GetValueOrDefault(l2);
                    if (threads is not null)
                    {
                        sb.Append("<tr class=\"comments\"><td></td><td></td><td>");
                        foreach (var c in threads)
                            sb.Append(Thread(c, replies, cv!, request.ReplyTo, draft, pr));
                        sb.Append("</td></tr>");
                    }
                    if (cv is { Remote: not null } && request.Target == $"{i}-{(oldSide ? 'o' : 'n')}-{l2}")
                    {
                        sb.Append("<tr class=\"comments\"><td></td><td></td><td>")
                            .Append(CommentForm(cv, pr, i, oldSide ? file.OldPath : file.Path, l2, oldSide, null, request.FindingId, draft))
                            .Append("</td></tr>");
                    }
                }
            }
            sb.Append("</table></details>");
        }
        return sb.ToString();
    }

    private static string AddLink(CommentView cv, int file, bool oldSide, int line) =>
        $"<a class=\"add-c\" href=\"{cv.DiffUrl}?c={file}-{(oldSide ? 'o' : 'n')}-{line}#c\" title=\"Comentar esta línea en Bitbucket\">+</a>";

    /// <summary>A comment with its replies, and the reply form when it was asked for.</summary>
    private string Thread(PrComment root, ILookup<long, PrComment> replies, CommentView cv, long? replyTo, string? draft, PullRequest pr)
    {
        var sb = new StringBuilder("<div class=\"thread\">");
        var ids = new List<long>();
        void Append(PrComment c, int depth)
        {
            ids.Add(c.Id);
            sb.Append("<div class=\"comment\" id=\"comment-").Append(c.Id).Append('"').Append(depth > 0 ? " style=\"margin-left:18px\"" : "").Append('>')
                .Append("<div class=\"muted\"><b>").Append(E(c.Author)).Append("</b> · ").Append(Date(c.CreatedAt));
            if (c.Url.Length > 0)
                sb.Append(" · <a href=\"").Append(E(c.Url)).Append("\" rel=\"noreferrer\">Bitbucket</a>");
            sb.Append("</div>").Append(Markdown(c.Body)).Append("</div>");
            foreach (var reply in replies[c.Id].OrderBy(r => r.CreatedAt))
                Append(reply, depth + 1);
        }
        Append(root, 0);

        if (replyTo is { } target && ids.Contains(target))
            sb.Append(CommentForm(cv, pr, null, null, null, false, root.Id, null, draft));
        else
            sb.Append("<a class=\"muted\" href=\"").Append(cv.DiffUrl).Append("?reply=").Append(root.Id).Append("#c\">Responder</a>");
        return sb.Append("</div>").ToString();
    }

    private string CommentForm(CommentView cv, PullRequest pr, int? file, string? path, int? line, bool oldSide, long? parent, string? findingId, string? draft)
    {
        var back = parent is not null ? $"reply-{parent}" : line is { } l ? $"{file}-{(oldSide ? 'o' : 'n')}-{l}" : $"{file}-f";
        var where = parent is not null ? "Respuesta" : line is { } n ? $"Comentario en {path}:{n}{(oldSide ? " (línea borrada)" : "")}" : $"Comentario en {path}";
        var sb = new StringBuilder();
        sb.Append("<form method=\"post\" action=\"").Append(PrtWebServer.PrUrl(pr.Ref)).Append("/comments\" class=\"comment-form\" id=\"c\">").Append(Csrf(token))
            .Append("<div class=\"muted\">").Append(E(where)).Append(findingId is null ? "" : $" · hallazgo {E(findingId)}").Append("</div>")
            .Append(Hidden("path", path)).Append(Hidden("line", line?.ToString(CultureInfo.InvariantCulture)))
            .Append(Hidden("side", oldSide ? "old" : "new")).Append(Hidden("parent", parent?.ToString(CultureInfo.InvariantCulture)))
            .Append(Hidden("finding", findingId)).Append(Hidden("back", back))
            .Append("<textarea name=\"body\" required autofocus placeholder=\"Comentario (Markdown)\">").Append(E(draft)).Append("</textarea>")
            .Append("<div class=\"row\"><button class=\"primary\">Comentar en Bitbucket</button><a href=\"").Append(cv.DiffUrl).Append("\">Cancelar</a>")
            .Append("<span class=\"muted\">Se publica con tu usuario de Bitbucket.</span></div></form>");
        return sb.ToString();
    }

    private static string Hidden(string name, string? value) =>
        value is null ? "" : $"<input type=\"hidden\" name=\"{name}\" value=\"{E(value)}\">";

    private static string FindingNote(string reviewer, Finding f, CommentView? cv = null, IReadOnlyDictionary<string, PrLineComment>? published = null)
    {
        var action = "";
        if (published?.GetValueOrDefault(f.Id) is { } done)
            action = $" · <span class=\"v-ok\">✓ comentado</span>{(done.Url.Length > 0 ? $" <a href=\"{E(done.Url)}\" rel=\"noreferrer\">ver</a>" : "")}";
        else if (cv is { Remote: not null } && f.File is not null)
            action = $" · <a href=\"{cv.DiffUrl}?f={Uri.EscapeDataString(f.Id)}#c\">Comentar en Bitbucket</a>";
        return $"<div class=\"finding-note\">{SeverityBadge(f.Severity)}<b>{E(f.Id)}</b> {E(f.Title)} <span class=\"muted\">· {E(reviewer)}" +
               (f.Line is { } line ? $" · línea {line}" : "") + $"{action}</span>" +
               (f.Recommendation.Length > 0 ? $"<div class=\"muted\">{E(f.Recommendation)}</div>" : "") + "</div>";
    }

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
