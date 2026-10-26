using System.CommandLine;
using System.Globalization;
using Prt.Core.Domain;
using Prt.Core.Panel;
using Prt.Core.Review;
using Spectre.Console;

namespace Prt.Cli.Commands;

/// <summary>PR groups: features that span several repos, reviewed together by the panel.</summary>
internal static class GroupCommands
{
    public static Command Group()
    {
        var command = new Command("group", "Grupos de PRs de varios repos que implementan una misma feature y se revisan en conjunto");
        command.Subcommands.Add(Create());
        command.Subcommands.Add(Add());
        command.Subcommands.Add(Remove());
        command.Subcommands.Add(List());
        command.Subcommands.Add(Show());
        command.Subcommands.Add(Analyze());
        command.Subcommands.Add(Delete());
        return command;
    }

    private static Argument<string> GroupArgument() => new("grupo") { Description = "Id o nombre del grupo" };

    private static Argument<string[]> PrsArgument(int min) => new("prs")
    {
        Description = "PRs: URL de Bitbucket o workspace/repo#numero",
        Arity = min == 0 ? ArgumentArity.ZeroOrMore : ArgumentArity.OneOrMore,
    };

    private static Command Create()
    {
        var name = new Argument<string>("nombre") { Description = "Nombre del grupo (ej. la rama de la feature)" };
        var prs = PrsArgument(0);
        var description = new Option<string?>("--description", "-d") { Description = "Qué hace la feature y cómo se relacionan los repos" };
        var command = new Command("create", "Crea un grupo con los PRs indicados (registra los que falten)") { name, prs, description };
        command.SetAction((result, ct) => Cli.Run(async app =>
        {
            var group = app.Store.CreateGroup(result.GetValue(name)!, result.GetValue(description) ?? "");
            app.Ui.Success($"Grupo {group.Id}: {group.Name}");
            await AddAsync(app, group, result.GetValue(prs) ?? [], ct);
            ShowSuggestions(app, group);
        }));
        return command;
    }

    private static Command Add()
    {
        var group = GroupArgument();
        var prs = PrsArgument(1);
        var command = new Command("add", "Agrega PRs a un grupo (registra los que falten)") { group, prs };
        command.SetAction((result, ct) => Cli.Run(async app =>
        {
            var target = Require(app, result.GetValue(group)!);
            await AddAsync(app, target, result.GetValue(prs)!, ct);
            ShowSuggestions(app, target);
        }));
        return command;
    }

    private static Command Remove()
    {
        var group = GroupArgument();
        var prs = PrsArgument(1);
        var command = new Command("remove", "Quita PRs de un grupo") { group, prs };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var target = Require(app, result.GetValue(group)!);
            foreach (var text in result.GetValue(prs)!)
            {
                var pr = app.RequireTracked(text);
                app.Store.RemoveGroupMember(target.Id, pr.Id);
                app.Exporter?.Export(app.Store, pr.Id);
                app.Ui.Success($"{pr.Key} ya no está en {target.Name}.");
            }
            app.Exporter?.ExportGroup(app.Store, target.Id);
            return Task.CompletedTask;
        }));
        return command;
    }

    private static Command List()
    {
        var command = new Command("list", "Lista los grupos");
        command.SetAction((_, _) => Cli.Run(app =>
        {
            var groups = app.Store.ListGroups();
            if (groups.Count == 0)
            {
                app.Ui.Info("No hay grupos. Creá uno con `prt group create <nombre> <pr> <pr>…`.");
                return Task.CompletedTask;
            }
            var table = new Table().Border(Symbols.Border).AddColumns("Id", "Grupo", "PRs", "Veredicto");
            foreach (var g in groups)
            {
                table.AddRow(g.Id.ToString(CultureInfo.InvariantCulture), Markup.Escape(g.Name),
                    Markup.Escape(string.Join(", ", app.Store.ListGroupMembers(g.Id).Select(m => m.Key))), Markup.Escape(g.Verdict ?? ""));
            }
            AnsiConsole.Write(table);
            return Task.CompletedTask;
        }));
        return command;
    }

    private static Command Show()
    {
        var group = GroupArgument();
        var command = new Command("show", "Muestra los PRs del grupo, el último análisis conjunto y los PRs sugeridos") { group };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var target = Require(app, result.GetValue(group)!);
            AnsiConsole.Write(new Rule($"[bold]Grupo {target.Id} · {Markup.Escape(target.Name)}[/] · {Markup.Escape(target.Verdict ?? "sin veredicto")}").LeftJustified());
            if (target.Description.Length > 0)
                AnsiConsole.MarkupLine(Markup.Escape(target.Description));

            var table = new Table().Border(Symbols.Border).AddColumns("PR", "Título", "Ramas", "Estado", "Veredicto");
            foreach (var pr in app.Store.ListGroupMembers(target.Id))
                table.AddRow(Markup.Escape(pr.Key), Markup.Escape(pr.Title), Markup.Escape($"{pr.SourceBranch} → {pr.DestinationBranch}"),
                    Markup.Escape(pr.State), Markup.Escape(pr.Verdict ?? ""));
            AnsiConsole.Write(table);

            var runs = app.Store.ListGroupRuns(target.Id);
            if (runs.Count > 0)
                PanelCommands.RenderRun($"Grupo {target.Name}", RunView.From(runs[^1]));
            else
                app.Ui.Info($"Sin análisis conjunto todavía: `prt group analyze {target.Id}`.");
            ShowSuggestions(app, target);
            return Task.CompletedTask;
        }));
        return command;
    }

    private static Command Analyze()
    {
        var group = GroupArgument();
        var reviewers = new Option<string[]>("--reviewers", "-r")
        {
            Description = "Revisores a correr (ej. -r coordination -r security). Por defecto, los habilitados (globales y de grupo)",
            AllowMultipleArgumentsPerToken = true,
        };
        var agent = ReviewCommands.AgentOption();
        agent.Description = "Forzar un agente para todos los revisores";
        var fresh = new Option<bool>("--new") { Description = "Sesiones nuevas para cada revisor en lugar de retomar las anteriores" };
        var parallel = new Option<int?>("--parallel") { Description = "Revisores en paralelo (por defecto, maxParallel global o 4)" };

        var command = new Command("analyze", "Corre el panel sobre todos los PRs del grupo en conjunto, con el revisor de coordinación entre repos")
        {
            group, reviewers, agent, fresh, parallel,
        };
        command.SetAction((result, ct) => Cli.Run(async app =>
        {
            app.WarnIfNoVault();
            var target = Require(app, result.GetValue(group)!);
            var names = result.GetValue(reviewers)?
                .SelectMany(r => r.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(r => r.ToLowerInvariant()).Distinct().ToList();
            foreach (var member in app.Store.ListGroupMembers(target.Id))
                app.EnsureCheckout(member.Ref);

            var outcome = await app.GroupPanel.AnalyzeAsync(target.Id,
                new PanelOptions(names, result.GetValue(agent), result.GetValue(fresh), result.GetValue(parallel)), ct);

            AnsiConsole.WriteLine();
            PanelCommands.RenderRun($"Grupo {target.Name}", RunView.From(app.Store.ListGroupRuns(target.Id)[^1]));
            foreach (var (key, verdict) in outcome.PrVerdicts)
                AnsiConsole.MarkupLine(Markup.Escape($"{key}: {(verdict is { } v ? ReviewStandard.Label(v) : "sin reportes válidos")}"));
            if (outcome.NotePath is not null)
                app.Ui.Info($"Nota: {outcome.NotePath}");
        }));
        return command;
    }

    private static Command Delete()
    {
        var group = GroupArgument();
        var command = new Command("delete", "Borra el grupo y sus análisis conjuntos (los PRs y sus análisis quedan)") { group };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var target = Require(app, result.GetValue(group)!);
            var members = app.Store.ListGroupMembers(target.Id);
            app.Store.DeleteGroup(target.Id);
            foreach (var pr in members)
                app.Exporter?.Export(app.Store, pr.Id);
            app.Ui.Success($"Grupo {target.Name} borrado.");
            return Task.CompletedTask;
        }));
        return command;
    }

    private static PrGroup Require(App app, string text) =>
        app.Store.FindGroup(text) ?? throw new UserException($"No existe el grupo '{text}'. Mirá los existentes con `prt group list`.");

    private static async Task AddAsync(App app, PrGroup group, IEnumerable<string> texts, CancellationToken ct)
    {
        foreach (var text in texts)
        {
            var prRef = PrRef.Parse(text);
            var pr = app.Store.FindPullRequest(prRef)
                     ?? (await new PullRequestSync(app.Store, app.Bitbucket, app.Ui).RefreshAsync(prRef, ct)).Pr;
            app.Store.AddGroupMembers(group.Id, [pr.Id]);
            app.Exporter?.Export(app.Store, pr.Id);
            app.Ui.Success($"{pr.Key} · {pr.Title} ({pr.SourceBranch})");
        }
        app.Exporter?.ExportGroup(app.Store, group.Id);
    }

    private static void ShowSuggestions(App app, PrGroup group)
    {
        var suggestions = app.Store.GroupSuggestions(group.Id);
        if (suggestions.Count == 0)
            return;
        app.Ui.Info("PRs registrados de otros repos con la misma rama (podrían ser parte de la feature):");
        foreach (var pr in suggestions)
            AnsiConsole.MarkupLine(Markup.Escape($"   {pr.Key} · {pr.Title} ({pr.SourceBranch})"));
        app.Ui.Info($"Agregalos con: prt group add {group.Id} {string.Join(' ', suggestions.Select(p => p.Key))}");
    }
}
