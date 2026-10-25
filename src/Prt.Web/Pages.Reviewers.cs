using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Prt.Core.Config;
using Prt.Core.Panel;
using static Prt.Web.Html;

namespace Prt.Web;

/// <summary>Editor of the panel reviewers: global (all projects) or per project.</summary>
internal sealed partial class Pages
{
    /// <summary>Where an edit can be saved, as offered in the form. <see cref="RepoKey"/> is null for the global layer.</summary>
    internal sealed record Target(string Key, string Label, string Dir, ProfileLayer Layer, string? RepoKey);

    private sealed record Scope(string? RepoKey, ReviewProfile Profile, IReadOnlyList<Target> Targets, string? RepoDir, string? UserDir, string? ReadOnlyRepoDir)
    {
        public string BasePath => RepoKey is null ? "/reviewers" : Url("repos", RepoKey.Split('/')[0], RepoKey.Split('/')[1], "reviewers");
        public string Title => RepoKey is null ? "Revisores globales" : $"Revisores de {RepoKey}";
    }

    private Scope ScopeFor(string? repoKey)
    {
        var config = context.LoadConfig();
        if (repoKey is null)
        {
            // From the global view any registered project can be the destination too.
            var targets = new List<Target> { new("global", "Global (todos los proyectos)", context.ConfigDir, ProfileLayer.Global, null) };
            foreach (var repo in KnownRepos(config))
                targets.AddRange(ProjectTargets(config, repo, named: true));
            return new Scope(null, context.GlobalProfile(), targets, null, null, null);
        }

        var user = ProfileLoader.UserProjectDir(repoKey, context.ConfigDir);
        var local = ProfileLoader.RepoDir(config.RepoPathFor(repoKey));
        var readOnlyRepo = local is null ? ProfileLoader.RepoDir(context.ProfileCheckout(config, repoKey)) : null;
        return new Scope(repoKey, context.Profile(config, repoKey), ProjectTargets(config, repoKey, named: false),
            local ?? readOnlyRepo, user, readOnlyRepo);
    }

    private List<Target> ProjectTargets(PrtConfig config, string repoKey, bool named)
    {
        var prefix = named ? $"{repoKey} — " : "";
        var targets = new List<Target>
        {
            new($"user:{repoKey}", $"{prefix}{(named ? "personal" : "Personal para este proyecto")} (no se commitea)",
                ProfileLoader.UserProjectDir(repoKey, context.ConfigDir), ProfileLayer.User, repoKey),
        };
        if (ProfileLoader.RepoDir(config.RepoPathFor(repoKey)) is { } local)
            targets.Add(new($"repo:{repoKey}", $"{prefix}{(named ? "repo" : "Repo")}, en {local} (para commitear y compartir con el equipo)",
                local, ProfileLayer.Repo, repoKey));
        return targets;
    }

    /// <summary>The profile a save into <paramref name="target"/> changes (to validate it and keep review.json consistent).</summary>
    private ReviewProfile ProfileOf(Target target) =>
        target.RepoKey is null ? context.GlobalProfile() : context.Profile(context.LoadConfig(), target.RepoKey);

    private string Origin(Scope scope, ReviewerDefinition reviewer) =>
        ProfileLoader.LayerOf(reviewer.Source, context.ConfigDir, scope.RepoDir, scope.UserDir) switch
        {
            ProfileLayer.BuiltIn => "incorporado",
            ProfileLayer.Global => scope.RepoKey is null ? "global" : "global (heredado)",
            ProfileLayer.Repo => scope.ReadOnlyRepoDir is not null ? "repo (.prt/ del clon gestionado, solo lectura)" : "repo (.prt/)",
            _ => "personal",
        };

    public string Reviewers(string? repoKey, string? message)
    {
        var scope = ScopeFor(repoKey);
        var sb = new StringBuilder();
        sb.Append("<h1>").Append(E(scope.Title)).Append("</h1>");
        sb.Append(repoKey is null
            ? "<p class=\"muted\">Aplican a todos los proyectos que no los personalicen. Parten de los revisores incorporados; lo que guardes como global queda en "
              + $"<code>{E(context.ConfigDir)}</code>. ¿Solo para un proyecto? Elegilo en <b>Guardar en</b> al crear o editar un revisor, "
              + "o entrá a <a href=\"/repos\">Repos</a> → Revisores del proyecto.</p>"
            : "<p class=\"muted\">El proyecto hereda los revisores globales (<a href=\"/reviewers\">ver</a>). Lo que personalices acá vale solo para este repo: "
              + "en tu carpeta personal, o en el <code>.prt/</code> del checkout para compartirlo con el equipo.</p>");
        if (message is not null)
            sb.Append("<div class=\"job\">").Append(E(message)).Append("</div>");

        sb.Append("<table><tr><th>Revisor</th><th>Título</th><th>Agente</th><th>Activo</th><th>Orden</th><th>Origen</th><th></th></tr>");
        var position = 0;
        foreach (var r in scope.Profile.Reviewers)
        {
            position++;
            var agent = r.Agent ?? scope.Profile.DefaultAgent;
            sb.Append("<tr><td><b>").Append(E(r.Name)).Append("</b> <span class=\"muted\">").Append(E(r.IdPrefix)).Append("</span></td>")
                .Append("<td>").Append(E(r.Title)).Append("</td>")
                .Append("<td>").Append(agent is null ? "<span class=\"muted\">por defecto</span>" : E(agent)).Append("</td>")
                .Append("<td>").Append(r.Enabled ? "sí" : "<span class=\"muted\">no</span>").Append("</td>")
                .Append("<td>").Append(scope.Profile.ExplicitReviewers is null ? r.Order.ToString(CultureInfo.InvariantCulture) : $"{position} <span class=\"muted\">(review.json)</span>").Append("</td>")
                .Append("<td title=\"").Append(E(r.Source)).Append("\">").Append(E(Origin(scope, r))).Append("</td>")
                .Append("<td><a href=\"").Append(scope.BasePath).Append("/edit?name=").Append(Uri.EscapeDataString(r.Name)).Append("\">Editar</a></td></tr>");
        }
        sb.Append("</table>");
        sb.Append("<p><a href=\"").Append(scope.BasePath).Append("/edit?new=1\">+ Nuevo revisor</a></p>");
        if (scope.ReadOnlyRepoDir is not null)
            sb.Append("<p class=\"muted\">Este repo no tiene checkout local: el <code>.prt/</code> commiteado se lee del clon gestionado, pero para editarlo hay que asociar un checkout en <a href=\"/repos\">Repos</a>.</p>");
        return Page(scope.Title, sb.ToString());
    }

    public string ReviewerForm(string? repoKey, string? name, bool isNew, string? error = null, ReviewerDefinition? draft = null, string? target = null)
    {
        var scope = ScopeFor(repoKey);
        var current = name is null ? null : scope.Profile.Reviewers.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!isNew && current is null && draft is null)
            throw new ArgumentException($"No existe el revisor '{name}'.");

        var r = draft ?? current ?? new ReviewerDefinition
        {
            Name = "",
            Title = "",
            IdPrefix = "",
            Instructions = "Revisás …\n\nPrestá atención a:\n- …\n\nFuera de tu foco: …",
            Order = (scope.Profile.Reviewers.Count == 0 ? 0 : scope.Profile.Reviewers.Max(x => x.Order)) + 1,
        };
        var effectiveAgent = r.Agent;
        var sb = new StringBuilder();
        sb.Append("<p><a href=\"").Append(scope.BasePath).Append("\">← ").Append(E(scope.Title)).Append("</a></p>");
        sb.Append("<h1>").Append(isNew ? "Nuevo revisor" : $"Revisor {E(r.Name)}").Append("</h1>");
        if (!isNew && current is not null)
            sb.Append("<p class=\"muted\">Ahora sale de: ").Append(E(Origin(scope, current))).Append(" <code>").Append(E(current.Source)).Append("</code></p>");
        if (error is not null)
            sb.Append("<div class=\"job job-failed\">").Append(E(error)).Append("</div>");

        sb.Append("<form method=\"post\" action=\"").Append(scope.BasePath).Append("/save\" class=\"card\">").Append(Csrf(token))
            .Append("<input type=\"hidden\" name=\"new\" value=\"").Append(isNew ? "1" : "0").Append("\">");
        sb.Append("<p><label>Nombre<br>");
        sb.Append(isNew
            ? $"<input name=\"name\" value=\"{E(r.Name)}\" placeholder=\"pci\" required pattern=\"[a-z0-9][a-z0-9-]*\">"
            : $"<input type=\"hidden\" name=\"name\" value=\"{E(r.Name)}\"><code>{E(r.Name)}</code>");
        sb.Append("</label></p>");
        sb.Append("<div class=\"row\"><label>Título<br><input name=\"title\" size=\"40\" value=\"").Append(E(r.Title)).Append("\" required></label>")
            .Append("<label>Prefijo de hallazgos<br><input name=\"id_prefix\" size=\"8\" value=\"").Append(E(r.IdPrefix)).Append("\" required></label>")
            .Append("<label>Orden<br><input name=\"order\" type=\"number\" style=\"width:80px\" value=\"").Append(r.Order).Append("\"></label>")
            .Append("<label>Agente<br><select name=\"agent\"><option value=\"\">Por defecto").Append(scope.Profile.DefaultAgent is { } d ? $" ({E(d)})" : "").Append("</option>");
        foreach (var agent in AgentNames.All)
            sb.Append("<option").Append(string.Equals(agent, effectiveAgent, StringComparison.OrdinalIgnoreCase) ? " selected" : "").Append('>').Append(agent).Append("</option>");
        sb.Append("</select></label><label><br><input type=\"checkbox\" name=\"enabled\"").Append(r.Enabled ? " checked" : "").Append("> Activo (corre por defecto en <code>analyze</code>)</label></div>");
        sb.Append("<p><label>Instrucciones (foco del revisor, en Markdown)<br><textarea name=\"instructions\" style=\"min-height:280px\" required>")
            .Append(E(r.Instructions)).Append("</textarea></label></p>");

        sb.Append("<p>Guardar en:</p>");
        var selected = target ?? scope.Targets[0].Key;
        if (scope.Targets.Count <= 3)
        {
            foreach (var t in scope.Targets)
            {
                sb.Append("<label style=\"display:block\"><input type=\"radio\" name=\"target\" value=\"").Append(E(t.Key)).Append('"')
                    .Append(t.Key == selected ? " checked" : "").Append("> ").Append(E(t.Label)).Append("</label>");
            }
        }
        else
        {
            // Many projects: one select, grouped by project.
            sb.Append("<select name=\"target\">");
            foreach (var group in scope.Targets.GroupBy(t => t.RepoKey))
            {
                if (group.Key is not null)
                    sb.Append("<optgroup label=\"").Append(E(group.Key)).Append("\">");
                foreach (var t in group)
                    sb.Append("<option value=\"").Append(E(t.Key)).Append('"').Append(t.Key == selected ? " selected" : "").Append('>').Append(E(t.Label)).Append("</option>");
                if (group.Key is not null)
                    sb.Append("</optgroup>");
            }
            sb.Append("</select>");
        }
        if (repoKey is null && scope.Targets.Count > 1)
            sb.Append("<p class=\"muted\">Si elegís un proyecto, el revisor vale solo ahí y los demás siguen usando el global.</p>");
        if (repoKey is not null && scope.Targets.All(t => t.Layer != ProfileLayer.Repo))
            sb.Append("<p class=\"muted\">Para guardarlo en el <code>.prt/</code> del repo, asociá un checkout local en <a href=\"/repos\">Repos</a>.</p>");
        sb.Append("<p><button class=\"primary\">Guardar</button></p></form>");

        if (!isNew)
        {
            foreach (var t in scope.Targets.Where(t => ReviewerFiles.Exists(t.Dir, r.Name)))
            {
                var builtIn = ProfileLoader.Load([]).Reviewers.Any(b => b.Name == r.Name);
                var label = t.Layer switch
                {
                    ProfileLayer.Global => builtIn ? "Volver al revisor incorporado" : "Eliminar este revisor global",
                    _ => $"Quitar la personalización {(t.Layer == ProfileLayer.Repo ? "del repo" : "personal")}"
                         + (repoKey is null ? $" de {t.RepoKey}" : ""),
                };
                sb.Append("<form method=\"post\" class=\"inline\" action=\"").Append(scope.BasePath).Append("/remove\">").Append(Csrf(token))
                    .Append("<input type=\"hidden\" name=\"name\" value=\"").Append(E(r.Name)).Append("\">")
                    .Append("<input type=\"hidden\" name=\"target\" value=\"").Append(E(t.Key)).Append("\">")
                    .Append("<button>").Append(E(label)).Append("</button></form> ");
            }
        }
        return Page(isNew ? "Nuevo revisor" : $"Revisor {r.Name}", sb.ToString());
    }

    public string SaveReviewer(string? repoKey, IFormCollection form)
    {
        var scope = ScopeFor(repoKey);
        var isNew = form["new"] == "1";
        var name = form["name"].ToString().Trim().ToLowerInvariant();
        var draft = new ReviewerDefinition
        {
            Name = name,
            Title = form["title"].ToString().Trim(),
            IdPrefix = form["id_prefix"].ToString().Trim().ToUpperInvariant(),
            Instructions = form["instructions"].ToString(),
            Enabled = form["enabled"] == "on",
            Order = int.TryParse(form["order"], CultureInfo.InvariantCulture, out var order) ? order : 100,
            Agent = form["agent"].ToString() is { Length: > 0 } agent ? agent.ToLowerInvariant() : null,
        };
        var targetKey = form["target"].ToString();
        var target = scope.Targets.FirstOrDefault(t => t.Key == targetKey);

        try
        {
            if (target is null)
                throw new ArgumentException("Elegí dónde guardar el revisor.");
            var destination = ProfileOf(target);
            if (isNew && destination.Reviewers.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Ya existe un revisor '{name}'{(target.RepoKey is null ? "" : $" en {target.RepoKey}")}: editalo desde la lista.");
            var path = ReviewerFiles.SaveEdited(target.Dir, draft, destination);
            // Show the list where the change applies.
            return Reviewers(target.RepoKey, $"Revisor {name} guardado en {path}.");
        }
        catch (ArgumentException ex)
        {
            return ReviewerForm(repoKey, name, isNew, ex.Message, draft, targetKey);
        }
    }

    public string RemoveReviewer(string? repoKey, IFormCollection form)
    {
        var scope = ScopeFor(repoKey);
        var name = form["name"].ToString();
        var target = scope.Targets.FirstOrDefault(t => t.Key == form["target"].ToString())
                     ?? throw new ArgumentException("Capa inválida.");

        ReviewerFiles.Delete(target.Dir, name);
        try
        {
            ProfileOf(target);
        }
        catch (ArgumentException)
        {
            // The reviewer is gone, but a review.json list in that layer still names it: drop it from there too.
            ReviewerFiles.UpdateSettings(target.Dir, s =>
            {
                s.Reviewers = s.Reviewers?.Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                s.Agents?.Remove(name);
            });
        }
        var layer = target.Layer switch
        {
            ProfileLayer.Global => "global",
            ProfileLayer.Repo => $"del repo de {target.RepoKey}",
            _ => $"personal de {target.RepoKey}",
        };
        return Reviewers(target.RepoKey, $"Se quitó {name} de la capa {layer}: ahora aplica lo heredado.");
    }
}
