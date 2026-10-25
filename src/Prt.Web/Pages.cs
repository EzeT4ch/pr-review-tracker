using System.Globalization;
using System.Text;
using Prt.Core.Bitbucket;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Panel;
using Prt.Core.Repos;
using static Prt.Web.Html;

namespace Prt.Web;

/// <summary>Server-rendered pages of the panel.</summary>
internal sealed partial class Pages(WebContext context, JobRunner jobs, string token, DiffCache diffs)
{
    private static readonly Severity[] Severities = [Severity.Blocker, Severity.Major, Severity.Minor, Severity.Info];

    // ---- Home ------------------------------------------------------------------------------

    public string Home(string? state, string? repo, string? query, string? error)
    {
        using var store = context.OpenStore();
        var all = store.ListPullRequests();
        var repos = all.Select(p => p.Ref.RepoKey).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var states = all.Select(p => p.State).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

        var prs = store.ListPullRequests(NullIfEmpty(state), NullIfEmpty(repo))
            .Where(p => string.IsNullOrWhiteSpace(query)
                        || p.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || p.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || p.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("<h1>Pull requests</h1>");
        if (error is not null)
            sb.Append("<div class=\"job job-failed\">").Append(E(error)).Append("</div>");

        sb.Append("<form method=\"post\" action=\"/track\" class=\"card row\">").Append(Csrf(token))
            .Append("<input name=\"pr\" size=\"60\" placeholder=\"https://bitbucket.org/workspace/repo/pull-requests/42 o workspace/repo#42\" required>")
            .Append("<button name=\"action\" value=\"sync\">Registrar</button>")
            .Append("<button class=\"primary\" name=\"action\" value=\"analyze\">Analizar con el panel</button></form>");

        sb.Append("<form method=\"get\" class=\"row\">")
            .Append(Select("state", "Todos los estados", states, state))
            .Append(Select("repo", "Todos los repos", repos, repo))
            .Append("<input name=\"q\" placeholder=\"Buscar título, PR o autor\" value=\"").Append(E(query)).Append("\">")
            .Append("<button>Filtrar</button></form>");

        if (prs.Count == 0)
        {
            sb.Append("<p class=\"muted\">No hay PRs ").Append(all.Count == 0 ? "registrados todavía." : "con ese filtro.").Append("</p>");
            return Page("PRs", sb.ToString());
        }

        sb.Append("<table><tr><th>PR</th><th>Título</th><th>Autor</th><th>Estado</th><th>Veredicto</th><th>Abiertos</th><th>Actividad</th></tr>");
        foreach (var pr in prs)
        {
            var runs = store.ListRuns(pr.Id);
            var running = jobs.Running(pr.Ref) is not null ? " <span class=\"muted\">⏳</span>" : "";
            sb.Append("<tr><td><a href=\"").Append(PrtWebServer.PrUrl(pr.Ref)).Append("\">").Append(E(pr.Key)).Append("</a>").Append(running).Append("</td>")
                .Append("<td>").Append(E(pr.Title)).Append("</td>")
                .Append("<td>").Append(E(pr.Author)).Append("</td>")
                .Append("<td>").Append(E(pr.State)).Append("</td>")
                .Append("<td>").Append(VerdictBadge(pr.Verdict)).Append("</td>")
                .Append("<td>").Append(runs.Count > 0 ? Counts(RunView.From(runs[^1])) : "").Append("</td>")
                .Append("<td class=\"muted\">").Append(Date(store.LastActivity(pr.Id))).Append("</td></tr>");
        }
        sb.Append("</table>");
        return Page("PRs", sb.ToString());
    }

    // ---- Pull request ----------------------------------------------------------------------

    public string PullRequest(PrRef prRef)
    {
        var config = context.LoadConfig();
        using var store = context.OpenStore();
        var job = jobs.Latest(prRef);
        var pr = store.FindPullRequest(prRef);
        if (pr is null)
        {
            var pending = job is not null ? JobBox(job) : "";
            return Page(prRef.Key, $"<h1>{E(prRef.Key)}</h1>{pending}<p class=\"muted\">Todavía no está registrado.</p>",
                job?.Status == JobStatus.Running ? 3 : null);
        }

        var h = store.GetHistory(pr.Id);
        var url = PrtWebServer.PrUrl(prRef);
        var sb = new StringBuilder();

        sb.Append("<h1>").Append(E(pr.Key)).Append(" · ").Append(E(pr.Title)).Append("</h1>");
        sb.Append("<div class=\"row muted\"><span>").Append(E(pr.Author)).Append("</span><span><code>").Append(E(pr.SourceBranch))
            .Append("</code> → <code>").Append(E(pr.DestinationBranch)).Append("</code></span><span>").Append(E(pr.State)).Append("</span>");
        if (pr.Url.Length > 0)
            sb.Append("<a href=\"").Append(E(pr.Url)).Append("\" rel=\"noreferrer\">Abrir en Bitbucket</a>");
        sb.Append("</div>");
        sb.Append("<p>Veredicto: ").Append(VerdictBadge(pr.Verdict)).Append(" &nbsp; ");
        foreach (var tag in h.Tags)
            sb.Append("<span class=\"tag\">#").Append(E(tag)).Append("</span>");
        sb.Append("</p>");

        if (job is not null)
            sb.Append(JobBox(job));

        // Actions.
        sb.Append("<div class=\"card\"><div class=\"row\">");
        sb.Append("<form method=\"post\" class=\"inline\" action=\"").Append(url).Append("/sync\">").Append(Csrf(token))
            .Append("<button>Actualizar desde Bitbucket</button></form>");
        sb.Append("<a href=\"").Append(url).Append("/diff\">Ver diff</a>");
        sb.Append("<span class=\"muted\">Revisión interactiva: <code>prt review ").Append(E(pr.Key)).Append("</code></span></div>");
        AppendAnalyzeForm(sb, config, pr, url);
        sb.Append("</div>");

        sb.Append("<p class=\"muted\">Código para los agentes: ").Append(CodeSource(config, prRef.RepoKey, pr.DestinationBranch))
            .Append(" · <a href=\"/repos#").Append(E(prRef.RepoKey)).Append("\">cambiar</a></p>");

        AppendPanel(sb, h, url);
        AppendSessions(sb, config, h);

        if (pr.Description.Trim().Length > 0)
            sb.Append("<details><summary>Descripción del PR</summary>").Append(Markdown(pr.Description)).Append("</details>");

        AppendTimeline(sb, h, url);
        AppendForms(sb, h, url);

        return Page(pr.Key, sb.ToString(), job?.Status == JobStatus.Running ? 5 : null);
    }

    private void AppendAnalyzeForm(StringBuilder sb, PrtConfig config, PullRequest pr, string url)
    {
        IReadOnlyList<ReviewerDefinition> reviewers;
        try
        {
            reviewers = context.Profile(config, pr.Ref.RepoKey).Reviewers;
        }
        catch (ArgumentException ex)
        {
            sb.Append("<p>Configuración de revisores inválida: ").Append(E(ex.Message)).Append("</p>");
            return;
        }

        sb.Append("<form method=\"post\" action=\"").Append(url).Append("/analyze\" class=\"row\" style=\"margin-top:10px\">").Append(Csrf(token));
        foreach (var r in reviewers)
        {
            sb.Append("<label><input type=\"checkbox\" name=\"reviewer\" value=\"").Append(E(r.Name)).Append('"')
                .Append(r.Enabled ? " checked" : "").Append("> ").Append(E(r.Title)).Append("</label>");
        }
        sb.Append("<select name=\"agent\"><option value=\"\">Agente según el proyecto</option>");
        foreach (var agent in AgentNames.All)
            sb.Append("<option>").Append(agent).Append("</option>");
        sb.Append("</select><label><input type=\"checkbox\" name=\"new\"> Sesiones nuevas</label>")
            .Append("<button class=\"primary\">Analizar con el panel</button>")
            .Append("<a href=\"").Append(Html.Url("repos", pr.Ref.Workspace, pr.Ref.Repo, "reviewers")).Append("\">configurar revisores</a></form>");
    }

    private static void AppendPanel(StringBuilder sb, PrHistory h, string url)
    {
        sb.Append("<h2>Panel de revisores</h2>");
        if (h.Runs.Count == 0)
        {
            sb.Append("<p class=\"muted\">Sin análisis todavía.</p>");
            return;
        }

        var view = RunView.From(h.Runs[^1]);
        sb.Append("<p>").Append(VerdictBadge(view.Run.Verdict)).Append(" · ").Append(Date(view.Run.StartedAt)).Append(" · ")
            .Append(Counts(view)).Append(" · <a href=\"").Append(url).Append("/run/").Append(view.Run.Id).Append("\">Reporte completo</a></p>");
        AppendReviewerTable(sb, view);
        AppendFindings(sb, view, detailed: false);
        AppendQuestions(sb, view);

        if (h.Runs.Count > 1)
        {
            sb.Append("<p class=\"muted\">Análisis anteriores: ");
            sb.Append(string.Join(" · ", h.Runs.Take(h.Runs.Count - 1).Reverse().Select(r =>
                $"<a href=\"{url}/run/{r.Run.Id}\">{Date(r.Run.StartedAt)} {E(r.Run.Verdict)}</a>")));
            sb.Append("</p>");
        }
    }

    private void AppendSessions(StringBuilder sb, PrtConfig config, PrHistory h)
    {
        sb.Append("<h2>Sesiones</h2>");
        if (h.Sessions.Count == 0)
        {
            sb.Append("<p class=\"muted\">Sin sesiones todavía.</p>");
            return;
        }
        sb.Append("<table><tr><th>Rol</th><th>Agente</th><th>Último uso</th><th>Retomar</th></tr>");
        foreach (var s in h.Sessions.OrderBy(s => s.Reviewer is not null).ThenBy(s => s.Reviewer, StringComparer.Ordinal))
        {
            var command = s.Reviewer is null
                ? context.ResumeCommand(config, s.Agent, s.AgentSessionId)
                : $"prt review {h.Pr.Key} --reviewer {s.Reviewer} --agent {s.Agent}";
            sb.Append("<tr><td>").Append(E(s.Reviewer ?? "revisión")).Append("</td><td>").Append(E(s.Agent)).Append(s.IsDefault ? " ⭐" : "")
                .Append("</td><td class=\"muted\">").Append(Date(s.LastUsedAt)).Append("</td><td><code>").Append(E(command)).Append("</code></td></tr>");
        }
        sb.Append("</table>");
    }

    private static void AppendTimeline(StringBuilder sb, PrHistory h, string url)
    {
        sb.Append("<h2 id=\"timeline\">Timeline</h2>");
        var sessions = h.Sessions.ToDictionary(s => s.Id);
        var roundIds = h.Rounds.Select(r => r.Id).ToHashSet();
        var entries = new List<(DateTimeOffset At, string Html)>();

        foreach (var round in h.Rounds)
        {
            var session = sessions.GetValueOrDefault(round.SessionId);
            var who = session is null ? "?" : session.Reviewer is null ? session.Agent : $"{session.Agent} ({session.Reviewer})";
            var item = new StringBuilder("<h3>").Append(Date(round.StartedAt)).Append(" — ").Append(E(who)).Append(" · ")
                .Append(round.Kind == RoundKinds.New ? "nueva sesión" : "sesión retomada");
            if (round.CommitAtStart is { Length: > 0 } commit)
                item.Append(" · <code>").Append(E(CommitHashes.Short(commit))).Append("</code>");
            item.Append("</h3>");
            if (round.Prompt is { Length: > 0 } prompt)
                item.Append("<details><summary class=\"muted\">Prompt enviado</summary><pre>").Append(E(prompt)).Append("</pre></details>");
            foreach (var memo in h.Memos.Where(m => m.RoundId == round.Id))
                item.Append(Memo(memo));
            entries.Add((round.StartedAt, item.ToString()));
        }

        foreach (var memo in h.Memos.Where(m => m.RoundId is null || !roundIds.Contains(m.RoundId.Value)))
            entries.Add((memo.CreatedAt, $"<h3>{Date(memo.CreatedAt)} — {E(MemoKinds.Label(memo.Kind))}</h3>{Memo(memo)}"));

        foreach (var run in h.Runs)
        {
            var view = RunView.From(run);
            entries.Add((run.Run.StartedAt,
                $"<h3>{Date(run.Run.StartedAt)} — Panel de revisores · {VerdictBadge(run.Run.Verdict)}</h3>" +
                $"<p>{Counts(view)} {view.Reviewers.Count} revisor(es) · <a href=\"{url}/run/{run.Run.Id}\">Reporte completo</a></p>"));
        }

        if (entries.Count == 0)
            sb.Append("<p class=\"muted\">Sin actividad todavía.</p>");
        foreach (var (_, item) in entries.OrderByDescending(e => e.At))
            sb.Append(item);
    }

    private static string Memo(Memo memo) =>
        $"<div class=\"memo memo-{memo.Kind}\"><div class=\"muted\">{E(MemoKinds.Label(memo.Kind))} · {Date(memo.CreatedAt)}</div>{Markdown(memo.Body)}</div>";

    private void AppendForms(StringBuilder sb, PrHistory h, string url)
    {
        sb.Append("<h2>Registrar</h2><div class=\"card\">");
        sb.Append("<form method=\"post\" action=\"").Append(url).Append("/memo\">").Append(Csrf(token))
            .Append("<textarea name=\"body\" placeholder=\"Nota o decisión (Markdown)\" required></textarea>")
            .Append("<div class=\"row\"><select name=\"kind\"><option value=\"note\">Nota</option><option value=\"decision\">Decisión</option></select>")
            .Append("<button class=\"primary\">Guardar</button></div></form>");

        sb.Append("<form method=\"post\" action=\"").Append(url).Append("/tags\" class=\"row\" style=\"margin-top:12px\">").Append(Csrf(token))
            .Append("<input name=\"add\" placeholder=\"tags separados por espacio\"><button>Agregar tags</button>");
        foreach (var tag in h.Tags)
            sb.Append("<button name=\"remove\" value=\"").Append(E(tag)).Append("\" title=\"Quitar\">#").Append(E(tag)).Append(" ✕</button>");
        sb.Append("</form></div>");
    }

    private string CodeSource(PrtConfig config, string repoKey, string? destination)
    {
        if (config.RepoPathFor(repoKey) is { } local)
            return $"checkout local <code>{E(local)}</code>";
        if (config.RepoPaths.TryGetValue(repoKey, out var missing))
            return $"<span class=\"v-block\">el checkout <code>{E(missing)}</code> no existe</span>";
        if (!config.ManagedClones)
            return "solo el diff (clones gestionados desactivados)";
        var managed = context.Managed(config);
        return managed.Exists(repoKey)
            ? $"clon gestionado por prt, en <code>{E(destination is { Length: > 0 } d ? d : "la rama de destino")}</code>"
            : "clon gestionado por prt (se crea en el próximo análisis)";
    }

    // ---- Repos -----------------------------------------------------------------------------

    public string Repos(string? focus, string? message)
    {
        var config = context.LoadConfig();
        List<PullRequest> prs;
        using (var store = context.OpenStore())
            prs = store.ListPullRequests().ToList();
        var repos = KnownRepos(config, prs);
        var managed = context.Managed(config);

        var sb = new StringBuilder("<h1>Repos</h1>");
        sb.Append("<p class=\"muted\">De dónde leen el código los agentes. Con un <b>checkout local</b> usan tu carpeta tal como está. ")
            .Append("Sin él, prt mantiene un <b>clon gestionado</b> de solo lectura en la rama de destino de cada PR (por ejemplo develop), ")
            .Append("actualizado en cada análisis, para que el diff se compare contra el proyecto tal como está hoy.</p>");
        if (message is not null)
            sb.Append("<div class=\"job\">").Append(E(message)).Append("</div>");
        if (repos.Count == 0)
            sb.Append("<p class=\"muted\">Todavía no hay repos: registrá un PR desde la página de PRs.</p>");

        foreach (var repoKey in repos)
        {
            var parts = repoKey.Split('/');
            var action = Html.Url("repos", parts[0], parts[1]);
            var destinations = prs.Where(p => string.Equals(p.Ref.RepoKey, repoKey, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.DestinationBranch).Where(b => b.Length > 0).Distinct().ToList();
            sb.Append("<div class=\"card\" id=\"").Append(E(repoKey)).Append("\"").Append(focus == repoKey ? " style=\"border-color:var(--link)\"" : "").Append('>');
            sb.Append("<h3>").Append(E(repoKey)).Append("</h3><p>Ahora: ").Append(CodeSource(config, repoKey, destinations.FirstOrDefault())).Append("</p>");

            if (managed.Exists(repoKey))
            {
                sb.Append("<p class=\"muted\">Clon gestionado: ").Append(managed.Size(repoKey))
                    .Append(" · ramas preparadas: ").Append(E(string.Join(", ", managed.Worktrees(repoKey)))).Append("</p>");
            }

            sb.Append("<form method=\"post\" action=\"").Append(action).Append("\" class=\"row\">").Append(Csrf(token))
                .Append("<input name=\"path\" size=\"60\" placeholder=\"C:\\workspace\\proyecto o /home/vos/proyecto\" value=\"")
                .Append(E(config.RepoPaths.GetValueOrDefault(repoKey))).Append("\">")
                .Append("<button name=\"action\" value=\"local\" class=\"primary\">Usar este checkout local</button></form>");

            sb.Append("<div class=\"row\" style=\"margin-top:8px\">");
            if (config.RepoPaths.ContainsKey(repoKey))
                sb.Append(Button(action, "managed", "Usar el clon gestionado"));
            if (config.ManagedClones && config.RepoPathFor(repoKey) is null && destinations.Count > 0)
                sb.Append(Button(action, "update", "Actualizar clon ahora"));
            if (managed.Exists(repoKey))
                sb.Append(Button(action, "delete", "Borrar clon gestionado"));
            sb.Append("<a href=\"").Append(action).Append("/reviewers\">Revisores del proyecto</a>");
            sb.Append("</div></div>");
        }
        return Page("Repos", sb.ToString());
    }

    /// <summary>Repos with registered PRs or a mapped checkout.</summary>
    private static List<string> KnownRepos(PrtConfig config, IEnumerable<PullRequest> prs) =>
        prs.Select(p => p.Ref.RepoKey).Concat(config.RepoPaths.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private List<string> KnownRepos(PrtConfig config)
    {
        using var store = context.OpenStore();
        return KnownRepos(config, store.ListPullRequests());
    }

    private string Button(string action, string value, string label) =>
        $"<form method=\"post\" class=\"inline\" action=\"{action}\">{Csrf(token)}<button name=\"action\" value=\"{value}\">{E(label)}</button></form>";

    // ---- Run report ------------------------------------------------------------------------

    public string Run(PrRef prRef, long runId)
    {
        using var store = context.OpenStore();
        var pr = store.FindPullRequest(prRef) ?? throw new ArgumentException($"{prRef.Key} no está registrado.");
        var run = store.ListRuns(pr.Id).FirstOrDefault(r => r.Run.Id == runId)
                  ?? throw new ArgumentException($"No existe el análisis {runId} de {prRef.Key}.");
        var view = RunView.From(run);
        var url = PrtWebServer.PrUrl(prRef);

        var sb = new StringBuilder();
        sb.Append("<p><a href=\"").Append(url).Append("\">← ").Append(E(pr.Key)).Append(" · ").Append(E(pr.Title)).Append("</a></p>");
        sb.Append("<h1>Análisis del panel · ").Append(Date(view.Run.StartedAt)).Append("</h1>");
        sb.Append("<p>").Append(VerdictBadge(view.Run.Verdict)).Append(" · ").Append(Counts(view));
        if (view.Run.HeadCommit is { Length: > 0 } head)
            sb.Append(" · head <code>").Append(E(CommitHashes.Short(head))).Append("</code>");
        if (view.Run.BaseBranch is { Length: > 0 } baseBranch)
            sb.Append(" · base <code>").Append(E(baseBranch)).Append("</code> @ <code>").Append(E(CommitHashes.Short(view.Run.BaseCommit)))
                .Append("</code> <span class=\"muted\">(clon gestionado)</span>");
        sb.Append("</p>");

        if (File.Exists(UnifiedDiff.RunDiffPath(context.RunsRoot, prRef, runId)))
            sb.Append("<p><a href=\"").Append(url).Append("/run/").Append(runId).Append("/diff\">Ver el diff analizado, con los hallazgos sobre el código</a></p>");
        AppendReviewerTable(sb, view);
        AppendFindings(sb, view, detailed: true);

        var resolved = view.ResolvedFindings.ToList();
        if (resolved.Count > 0)
        {
            sb.Append("<h2>Resueltos desde la pasada anterior</h2><ul>");
            foreach (var (reviewer, f) in resolved)
                sb.Append("<li>✅ ").Append(E(f.Id)).Append(" · ").Append(E(f.Title)).Append(" <span class=\"muted\">(").Append(E(reviewer)).Append(")</span></li>");
            sb.Append("</ul>");
        }

        AppendQuestions(sb, view);

        sb.Append("<h2>Resúmenes por revisor</h2>");
        foreach (var (result, report) in view.Reviewers)
        {
            sb.Append("<h3>").Append(E(result.Reviewer)).Append(" · ").Append(E(result.Agent)).Append("</h3>");
            sb.Append(report is not null
                ? Markdown(report.Summary)
                : $"<p class=\"v-block\">{(result.Status == ResultStatus.Invalid ? "Respuesta fuera de formato" : "Falló")}: {E(result.Error)}</p>");
            if (report is null && result.RawOutput is { Length: > 0 } raw)
                sb.Append("<details><summary class=\"muted\">Respuesta del agente</summary><pre>").Append(E(raw)).Append("</pre></details>");
        }
        return Page($"{pr.Key} · análisis", sb.ToString());
    }

    private static void AppendReviewerTable(StringBuilder sb, RunView view)
    {
        sb.Append("<table><tr><th>Revisor</th><th>Agente</th><th>Veredicto</th>");
        foreach (var s in Severities)
            sb.Append("<th>").Append(SeverityBadge(s)).Append("</th>");
        sb.Append("<th>Resueltos</th></tr>");
        foreach (var (result, report) in view.Reviewers)
        {
            sb.Append("<tr><td>").Append(E(result.Reviewer)).Append("</td><td>").Append(E(result.Agent)).Append("</td><td>");
            if (report is null)
            {
                sb.Append("<span class=\"v-block\">").Append(result.Status == ResultStatus.Invalid ? "formato inválido" : "falló")
                    .Append("</span></td>").Append(string.Concat(Enumerable.Repeat("<td>–</td>", Severities.Length + 1))).Append("</tr>");
                continue;
            }
            sb.Append(VerdictBadge(ReviewStandard.Label(report.Verdict))).Append("</td>");
            foreach (var s in Severities)
                sb.Append("<td>").Append(report.OpenFindings.Count(f => f.Severity == s)).Append("</td>");
            sb.Append("<td>").Append(report.Findings.Count(f => !f.IsOpen)).Append("</td></tr>");
        }
        sb.Append("</table>");
    }

    private static void AppendFindings(StringBuilder sb, RunView view, bool detailed)
    {
        var open = view.OpenFindings.ToList();
        sb.Append("<h2>Hallazgos abiertos</h2>");
        if (open.Count == 0)
        {
            sb.Append("<p class=\"muted\">Sin hallazgos abiertos.</p>");
            return;
        }

        foreach (var (reviewer, f) in open)
        {
            sb.Append(detailed ? "<details open>" : "<details>").Append("<summary>").Append(SeverityBadge(f.Severity)).Append("<b>")
                .Append(E(f.Id)).Append("</b> ").Append(E(f.Title)).Append(" <span class=\"muted\">· ").Append(E(reviewer));
            if (f.Location.Length > 0)
                sb.Append(" · <code>").Append(E(f.Location)).Append("</code>");
            sb.Append("</span></summary><div class=\"memo\">");
            sb.Append("<p class=\"muted\">");
            if (f.Category.Length > 0)
                sb.Append("Categoría: ").Append(E(f.Category)).Append(" · ");
            sb.Append("Confianza: ").Append(E(ReviewStandard.Label(f.Confidence))).Append(" · Estado: ").Append(E(ReviewStandard.Label(f.Status))).Append("</p>");
            sb.Append(Markdown(f.Description));
            if (f.Recommendation.Length > 0)
                sb.Append("<p><b>Recomendación:</b></p>").Append(Markdown(f.Recommendation));
            sb.Append("</div></details>");
        }
    }

    private static void AppendQuestions(StringBuilder sb, RunView view)
    {
        var questions = view.Questions.ToList();
        if (questions.Count == 0)
            return;
        sb.Append("<h2>Preguntas para el revisor humano</h2><ul>");
        foreach (var (reviewer, q) in questions)
            sb.Append("<li><b>").Append(E(reviewer)).Append(":</b> ").Append(E(q)).Append("</li>");
        sb.Append("</ul>");
    }

    // ---- Jobs ------------------------------------------------------------------------------

    public string Jobs()
    {
        var all = jobs.All;
        var sb = new StringBuilder("<h1>Tareas</h1>");
        if (all.Count == 0)
            sb.Append("<p class=\"muted\">No se lanzaron tareas desde que arrancó el servidor.</p>");
        foreach (var job in all)
            sb.Append(JobBox(job, link: true));
        return Page("Tareas", sb.ToString(), all.Any(j => j.Status == JobStatus.Running) ? 5 : null);
    }

    private static string JobBox(Job job, bool link = false)
    {
        var (css, label) = job.Status switch
        {
            JobStatus.Running => ("job-running", "en curso"),
            JobStatus.Failed => ("job-failed", "falló"),
            _ => ("", "terminada"),
        };
        var kind = job.Kind switch
        {
            "analyze" => "Análisis del panel",
            "clone" => "Actualización del clon gestionado",
            _ => "Actualización desde Bitbucket",
        };
        var title = !link ? ""
            : job.Pr.Number == 0 ? $"<a href=\"/repos#{E(job.Pr.RepoKey)}\">{E(job.Pr.RepoKey)}</a> · "
            : $"<a href=\"{PrtWebServer.PrUrl(job.Pr)}\">{E(job.Pr.Key)}</a> · ";
        var elapsed = (int)((job.EndedAt ?? DateTimeOffset.Now) - job.StartedAt).TotalSeconds;
        var when = job.EndedAt is { } end
            ? $"{job.StartedAt:HH:mm}–{end:HH:mm} ({elapsed} s)"
            : $"desde {job.StartedAt.ToString("HH:mm", CultureInfo.InvariantCulture)} ({elapsed} s)";
        return $"<div class=\"job {css}\"><b>{title}{kind}</b> · {label} · <span class=\"muted\">{when}</span>" +
               $"<details{(job.Status == JobStatus.Running ? " open" : "")}><summary class=\"muted\">Detalle</summary>" +
               $"<div class=\"log\">{E(string.Join('\n', job.Log))}</div></details></div>";
    }

    private static string Select(string name, string allLabel, IEnumerable<string> values, string? selected)
    {
        var sb = new StringBuilder("<select name=\"").Append(name).Append("\"><option value=\"\">").Append(E(allLabel)).Append("</option>");
        foreach (var value in values)
        {
            sb.Append("<option").Append(string.Equals(value, selected, StringComparison.OrdinalIgnoreCase) ? " selected" : "")
                .Append('>').Append(E(value)).Append("</option>");
        }
        return sb.Append("</select>").ToString();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
