using System.Globalization;
using System.Text;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Panel;
using static Prt.Web.Html;

namespace Prt.Web;

/// <summary>Jobs of a PR group are keyed by a pseudo PR, so the job runner keeps one per group at a time.</summary>
internal static class GroupJobs
{
    public const string Analyze = "group-analyze";
    public const string Sync = "group-sync";
    private const string Workspace = "_grupo";

    public static PrRef Ref(long groupId) => new(Workspace, groupId.ToString(CultureInfo.InvariantCulture), 0);

    public static long? GroupId(PrRef pr) =>
        pr.Workspace == Workspace && long.TryParse(pr.Repo, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}

/// <summary>PR groups: features that span several repos and are reviewed together.</summary>
internal sealed partial class Pages
{
    internal static string GroupUrl(long id) => "/groups/" + id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Marks a PR run that came from a joint analysis.</summary>
    private static string JointLabel(ReviewRun run) =>
        run.GroupRunId is null ? "" : " · <span class=\"tag\">análisis conjunto</span>";

    private void AppendGroups(StringBuilder sb, PrHistory h)
    {
        foreach (var group in h.Groups)
        {
            sb.Append("<div class=\"job\">Parte del grupo <a href=\"").Append(GroupUrl(group.Id)).Append("\"><b>").Append(E(group.Name))
                .Append("</b></a> ").Append(VerdictBadge(group.Verdict)).Append(" · se revisa en conjunto con los demás PRs del grupo.</div>");
        }
        if (h.Groups.Count == 0)
        {
            sb.Append("<details><summary class=\"muted\">¿Esta feature toca otros repos? Crear un grupo para revisarlos en conjunto</summary>")
                .Append("<form method=\"post\" action=\"/groups\" class=\"row\">").Append(Csrf(token))
                .Append("<input name=\"name\" required placeholder=\"Nombre del grupo\" value=\"").Append(E(h.Pr.SourceBranch)).Append("\">")
                .Append("<input type=\"hidden\" name=\"prs\" value=\"").Append(E(h.Pr.Key)).Append("\">")
                .Append("<button class=\"primary\">Crear grupo con este PR</button>")
                .Append("<span class=\"muted\">Después podés sumar los PRs de los otros repos (se sugieren los de la misma rama).</span></form></details>");
        }
    }

    // ---- List ------------------------------------------------------------------------------

    public string Groups(string? message)
    {
        using var store = context.OpenStore();
        var groups = store.ListGroups();
        var sb = new StringBuilder("<h1>Grupos de PRs</h1>");
        sb.Append("<p class=\"muted\">Una feature que toca varios repos se revisa en conjunto: cada revisor ve todos los PRs del grupo ")
            .Append("en una sola sesión, y un revisor de coordinación busca contratos rotos, cambios que faltan y problemas de despliegue entre repos. ")
            .Append("Cada PR recibe su parte de los hallazgos (los suyos y los transversales) y su propio veredicto.</p>");
        if (message is not null)
            sb.Append("<div class=\"job job-failed\">").Append(E(message)).Append("</div>");

        sb.Append("<form method=\"post\" action=\"/groups\" class=\"card\">").Append(Csrf(token))
            .Append("<div class=\"row\"><input name=\"name\" required size=\"40\" placeholder=\"Nombre (ej. feature/pagos-qr)\">")
            .Append("<input name=\"description\" size=\"60\" placeholder=\"Descripción (opcional)\"></div>")
            .Append("<textarea name=\"prs\" style=\"margin-top:8px;min-height:70px\" placeholder=\"PRs, uno por línea: URL de Bitbucket o workspace/repo#numero\"></textarea>")
            .Append("<button class=\"primary\">Crear grupo</button></form>");

        if (groups.Count == 0)
        {
            sb.Append("<p class=\"muted\">Todavía no hay grupos.</p>");
            return Page("Grupos", sb.ToString());
        }

        sb.Append("<table><tr><th>Grupo</th><th>PRs</th><th>Veredicto</th><th>Actualizado</th></tr>");
        foreach (var g in groups)
        {
            var members = store.ListGroupMembers(g.Id);
            var running = jobs.Running(GroupJobs.Ref(g.Id)) is not null ? " <span class=\"muted\">⏳</span>" : "";
            sb.Append("<tr><td><a href=\"").Append(GroupUrl(g.Id)).Append("\">").Append(E(g.Name)).Append("</a>").Append(running).Append("</td><td>")
                .Append(string.Join(", ", members.Select(m => $"<a href=\"{PrtWebServer.PrUrl(m.Ref)}\">{E(m.Key)}</a>")))
                .Append("</td><td>").Append(VerdictBadge(g.Verdict)).Append("</td><td class=\"muted\">").Append(Date(g.UpdatedAt)).Append("</td></tr>");
        }
        sb.Append("</table>");
        return Page("Grupos", sb.ToString());
    }

    // ---- Detail ----------------------------------------------------------------------------

    public string Group(long id, string? message)
    {
        var config = context.LoadConfig();
        using var store = context.OpenStore();
        var group = store.GetGroup(id) ?? throw new ArgumentException($"El grupo {id} no existe.");
        var members = store.ListGroupMembers(id);
        var runs = store.ListGroupRuns(id);
        var job = jobs.Latest(GroupJobs.Ref(id));
        var url = GroupUrl(id);

        var sb = new StringBuilder();
        sb.Append("<h1>Grupo · ").Append(E(group.Name)).Append("</h1>");
        sb.Append("<p>Veredicto del grupo: ").Append(VerdictBadge(group.Verdict)).Append("</p>");
        if (group.Description.Trim().Length > 0)
            sb.Append(Markdown(group.Description));
        if (message is not null)
            sb.Append("<div class=\"job\">").Append(E(message)).Append("</div>");
        if (job is not null)
            sb.Append(JobBox(job));

        // Members.
        sb.Append("<h2>PRs</h2>");
        if (members.Count == 0)
            sb.Append("<p class=\"muted\">El grupo no tiene PRs todavía.</p>");
        else
        {
            sb.Append("<table><tr><th>PR</th><th>Título</th><th>Ramas</th><th>Estado</th><th>Veredicto</th><th></th></tr>");
            foreach (var pr in members)
            {
                sb.Append("<tr><td><a href=\"").Append(PrtWebServer.PrUrl(pr.Ref)).Append("\">").Append(E(pr.Key)).Append("</a></td><td>")
                    .Append(E(pr.Title)).Append("</td><td><code>").Append(E(pr.SourceBranch)).Append("</code> → <code>").Append(E(pr.DestinationBranch))
                    .Append("</code></td><td>").Append(E(pr.State)).Append("</td><td>").Append(VerdictBadge(pr.Verdict)).Append("</td><td>")
                    .Append("<form method=\"post\" class=\"inline\" action=\"").Append(url).Append("/members\">").Append(Csrf(token))
                    .Append("<input type=\"hidden\" name=\"remove\" value=\"").Append(pr.Id).Append("\"><button title=\"Quitar del grupo\">Quitar</button></form>")
                    .Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        var suggestions = store.GroupSuggestions(id);
        if (suggestions.Count > 0)
        {
            sb.Append("<div class=\"card\"><b>Sugeridos</b> <span class=\"muted\">· PRs registrados de otros repos con la misma rama de origen</span><div class=\"row\" style=\"margin-top:6px\">");
            foreach (var pr in suggestions)
            {
                sb.Append("<form method=\"post\" class=\"inline\" action=\"").Append(url).Append("/members\">").Append(Csrf(token))
                    .Append("<input type=\"hidden\" name=\"add\" value=\"").Append(E(pr.Key)).Append("\"><button title=\"").Append(E(pr.Title)).Append("\">+ ")
                    .Append(E(pr.Key)).Append(" <span class=\"muted\">").Append(E(pr.SourceBranch)).Append("</span></button></form>");
            }
            sb.Append("</div></div>");
        }

        sb.Append("<form method=\"post\" action=\"").Append(url).Append("/members\" class=\"row\">").Append(Csrf(token))
            .Append("<input name=\"add\" size=\"60\" placeholder=\"Agregar PR: URL o workspace/repo#numero (varios separados por espacio)\">")
            .Append("<button>Agregar</button></form>");

        // Analysis.
        sb.Append("<div class=\"card\">");
        AppendGroupAnalyzeForm(sb, url, members.Count);
        sb.Append("</div>");

        AppendGroupRun(sb, runs, url);
        AppendGroupSessions(sb, config, store.ListGroupSessions(id));

        sb.Append("<details><summary class=\"muted\">Editar o borrar el grupo</summary><div class=\"card\">")
            .Append("<form method=\"post\" action=\"").Append(url).Append("/edit\">").Append(Csrf(token))
            .Append("<div class=\"row\"><input name=\"name\" required size=\"40\" value=\"").Append(E(group.Name)).Append("\"></div>")
            .Append("<textarea name=\"description\" style=\"margin-top:8px\" placeholder=\"Descripción (Markdown): qué hace la feature y cómo se relacionan los repos\">")
            .Append(E(group.Description)).Append("</textarea><button class=\"primary\">Guardar</button></form>")
            .Append("<form method=\"post\" action=\"").Append(url).Append("/delete\" style=\"margin-top:12px\">").Append(Csrf(token))
            .Append("<button>Borrar el grupo</button> <span class=\"muted\">Los PRs y sus análisis quedan; se pierden los análisis conjuntos.</span></form>")
            .Append("</div></details>");

        return Page($"Grupo {group.Name}", sb.ToString(), job?.Status == JobStatus.Running ? 5 : null);
    }

    private void AppendGroupAnalyzeForm(StringBuilder sb, string url, int memberCount)
    {
        if (memberCount < 2)
        {
            sb.Append("<p class=\"muted\">Agregá al menos 2 PRs para analizarlos en conjunto.</p>");
            return;
        }

        ReviewProfile profile;
        try
        {
            profile = context.GlobalProfile();
        }
        catch (ArgumentException ex)
        {
            sb.Append("<p>Configuración de revisores inválida: ").Append(E(ex.Message)).Append("</p>");
            return;
        }

        var reviewers = profile.Reviewers.Where(r => !profile.GroupReviewers.Any(g => g.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)))
            .Concat(profile.GroupReviewers).ToList();
        sb.Append("<form method=\"post\" action=\"").Append(url).Append("/analyze\" class=\"row\">").Append(Csrf(token));
        sb.Append("<select name=\"agent\"><option value=\"\">Agente según los revisores</option>");
        foreach (var agent in AgentNames.All)
            sb.Append("<option>").Append(agent).Append("</option>");
        sb.Append("</select><label><input type=\"checkbox\" name=\"new\"> Sesiones nuevas</label>")
            .Append("<button class=\"primary\">Analizar el grupo en conjunto</button>")
            .Append("<a href=\"/reviewers\">configurar revisores</a>")
            .Append(ReviewerPicker(reviewers, "Revisores globales más los de grupo (coordinación entre repos); el contexto de cada proyecto se suma igual."))
            .Append("</form>");
    }

    private static void AppendGroupRun(StringBuilder sb, IReadOnlyList<GroupRunHistory> runs, string url)
    {
        sb.Append("<h2>Análisis conjunto</h2>");
        if (runs.Count == 0)
        {
            sb.Append("<p class=\"muted\">Sin análisis todavía.</p>");
            return;
        }

        var view = RunView.From(runs[^1]);
        sb.Append("<p>").Append(VerdictBadge(view.Run.Verdict)).Append(" · ").Append(Date(view.Run.StartedAt)).Append(" · ")
            .Append(Counts(view)).Append(" · <a href=\"").Append(url).Append("/run/").Append(view.Run.Id).Append("\">Reporte completo</a></p>");
        AppendReviewerTable(sb, view);
        AppendFindings(sb, view, detailed: false);
        AppendQuestions(sb, view);

        if (runs.Count > 1)
        {
            sb.Append("<p class=\"muted\">Análisis anteriores: ");
            sb.Append(string.Join(" · ", runs.Take(runs.Count - 1).Reverse().Select(r =>
                $"<a href=\"{url}/run/{r.Run.Id}\">{Date(r.Run.StartedAt)} {E(r.Run.Verdict)}</a>")));
            sb.Append("</p>");
        }
    }

    private void AppendGroupSessions(StringBuilder sb, PrtConfig config, IReadOnlyList<GroupSession> sessions)
    {
        if (sessions.Count == 0)
            return;
        sb.Append("<h2>Sesiones</h2><table><tr><th>Revisor</th><th>Agente</th><th>Último uso</th><th>Retomar</th></tr>");
        foreach (var s in sessions)
        {
            sb.Append("<tr><td>").Append(E(s.Reviewer)).Append("</td><td>").Append(E(s.Agent)).Append("</td><td class=\"muted\">").Append(Date(s.LastUsedAt))
                .Append("</td><td><code>").Append(E($"cd {s.Cwd} && {context.ResumeCommand(config, s.Agent, s.AgentSessionId)}")).Append("</code></td></tr>");
        }
        sb.Append("</table>");
    }

    // ---- Run report ------------------------------------------------------------------------

    public string GroupRunReport(long id, long runId)
    {
        using var store = context.OpenStore();
        var group = store.GetGroup(id) ?? throw new ArgumentException($"El grupo {id} no existe.");
        var run = store.ListGroupRuns(id).FirstOrDefault(r => r.Run.Id == runId)
                  ?? throw new ArgumentException($"No existe el análisis {runId} del grupo {group.Name}.");
        var view = RunView.From(run);
        var url = GroupUrl(id);

        var sb = new StringBuilder();
        sb.Append("<p><a href=\"").Append(url).Append("\">← Grupo ").Append(E(group.Name)).Append("</a></p>");
        sb.Append("<h1>Análisis conjunto · ").Append(Date(view.Run.StartedAt)).Append("</h1>");
        sb.Append("<p>").Append(VerdictBadge(view.Run.Verdict)).Append(" · ").Append(Counts(view)).Append("</p>");
        sb.Append("<p class=\"muted\">PRs analizados: ");
        sb.Append(string.Join(" · ", GroupPanelService.ParseMembers(run.Run.Members).Select(m =>
        {
            var link = PrRef.TryParse(m.Key, out var prRef) ? $"<a href=\"{PrtWebServer.PrUrl(prRef)}\">{E(m.Key)}</a>" : E(m.Key);
            return m.Value is null ? link : $"{link} <code>{E(Prt.Core.Bitbucket.CommitHashes.Short(m.Value))}</code>";
        })));
        sb.Append("</p>");

        AppendReviewerTable(sb, view);
        AppendFindings(sb, view, detailed: true);

        var resolved = view.ResolvedFindings.ToList();
        if (resolved.Count > 0)
        {
            sb.Append("<h2>Resueltos desde la pasada anterior</h2><ul>");
            foreach (var (reviewer, f) in resolved)
                sb.Append("<li>✅ ").Append(E(f.Id)).Append(" · ").Append(E(f.Title)).Append(" <span class=\"muted\">(").Append(E(reviewer))
                    .Append(f.Pr is null ? "" : " · " + E(f.Pr)).Append(")</span></li>");
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
        return Page($"{group.Name} · análisis conjunto", sb.ToString());
    }
}
