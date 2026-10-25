using System.CommandLine;
using System.Text.Json;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Panel;
using Spectre.Console;

namespace Prt.Cli.Commands;

internal static class PanelCommands
{
    private static readonly Severity[] Severities = [Severity.Blocker, Severity.Major, Severity.Minor, Severity.Info];

    public static Command Analyze()
    {
        var pr = new Argument<string>("pr") { Description = "URL del PR en Bitbucket o workspace/repo#numero" };
        var reviewers = new Option<string[]>("--reviewers", "-r")
        {
            Description = "Revisores a correr (ej. -r security -r bugs, o -r security,bugs). Por defecto, los habilitados del proyecto",
            AllowMultipleArgumentsPerToken = true,
        };
        var agent = ReviewCommands.AgentOption();
        agent.Description = "Forzar un agente para todos los revisores";
        var fresh = new Option<bool>("--new") { Description = "Sesiones nuevas para cada revisor en lugar de retomar las anteriores" };
        var parallel = new Option<int?>("--parallel") { Description = "Revisores en paralelo (por defecto, maxParallel del proyecto o 4)" };

        var command = new Command("analyze", "Corre el panel de revisores (arquitectura, bugs, performance, seguridad…) sobre un PR")
        {
            pr, reviewers, agent, fresh, parallel,
        };
        command.SetAction((result, ct) => Cli.Run(async app =>
        {
            app.WarnIfNoVault();
            var names = result.GetValue(reviewers)?
                .SelectMany(r => r.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(r => r.ToLowerInvariant()).Distinct().ToList();
            var outcome = await app.Panel.AnalyzeAsync(PrRef.Parse(result.GetValue(pr)!),
                new PanelOptions(names, result.GetValue(agent), result.GetValue(fresh), result.GetValue(parallel)), ct);

            AnsiConsole.WriteLine();
            var history = app.Store.GetHistory(outcome.Pr.Id);
            RenderRun(outcome.Pr, RunView.From(history.Runs[^1]));
            if (outcome.NotePath is not null)
                app.Ui.Info($"Nota: {outcome.NotePath}");
            app.Ui.Info($"Para discutir un hallazgo con su revisor: prt review {outcome.Pr.Key} --reviewer <nombre>");
        }));
        return command;
    }

    public static Command Report()
    {
        var pr = HistoryCommands.PrArgument();
        var run = new Option<long?>("--run") { Description = "Id de la corrida (por defecto, la última)" };
        var json = new Option<bool>("--json") { Description = "Emitir los reportes normalizados en JSON" };
        var markdown = new Option<bool>("--markdown") { Description = "Emitir el reporte en Markdown" };

        var command = new Command("report", "Muestra el reporte del panel de revisores de un PR") { pr, run, json, markdown };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var target = app.RequireTracked(result.GetValue(pr));
            var runs = app.Store.ListRuns(target.Id);
            if (runs.Count == 0)
                throw new UserException($"{target.Key} no tiene análisis del panel. Corré `prt analyze {target.Key}`.");

            var selected = result.GetValue(run) is { } id
                ? runs.FirstOrDefault(r => r.Run.Id == id) ?? throw new UserException($"No existe la corrida {id} para {target.Key}.")
                : runs[^1];
            var view = RunView.From(selected);

            if (result.GetValue(json))
            {
                var payload = new
                {
                    pr = target.Key,
                    run = view.Run.Id,
                    verdict = view.Run.Verdict,
                    head = view.Run.HeadCommit,
                    reviewers = view.Reviewers.Select(r => new
                    {
                        reviewer = r.Result.Reviewer,
                        agent = r.Result.Agent,
                        status = r.Result.Status,
                        error = r.Result.Error,
                        report = r.Result.ReportJson is { } j ? JsonDocument.Parse(j).RootElement : (JsonElement?)null,
                    }),
                };
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            }
            else if (result.GetValue(markdown))
                Console.WriteLine(ReportRenderer.Render(view, ReportRenderer.DefaultDate));
            else
                RenderRun(target, view);
            return Task.CompletedTask;
        }));
        return command;
    }

    public static Command Reviewers()
    {
        var command = new Command("reviewers", "Revisores del panel y estándares de revisión por proyecto");
        command.Subcommands.Add(ListReviewers());
        command.Subcommands.Add(InitReviewers());
        return command;
    }

    private static Command ListReviewers()
    {
        var repo = new Argument<string?>("repo") { Description = "workspace/repo (por defecto, solo capas globales)", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("list", "Muestra los revisores que aplican a un proyecto y de dónde sale cada uno") { repo };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var repoKey = result.GetValue(repo)?.Trim();
            var profile = repoKey is null
                ? ProfileLoader.Load([ProfileLoader.GlobalDir])
                : ProfileLoader.Load(repoKey, app.Config.RepoPathFor(repoKey));

            var table = new Table().Border(TableBorder.Rounded).AddColumns("Revisor", "Título", "Prefijo", "Agente", "Activo", "Origen");
            foreach (var r in profile.Reviewers)
            {
                table.AddRow(
                    Markup.Escape(r.Name), Markup.Escape(r.Title), r.IdPrefix,
                    Markup.Escape(r.Agent ?? profile.DefaultAgent ?? app.Config.DefaultAgent),
                    r.Enabled ? "sí" : "[grey]no[/]",
                    Markup.Escape(r.Source));
            }
            AnsiConsole.Write(table);
            app.Ui.Info(profile.Layers.Count == 0
                ? "Solo estándares incorporados. Personalizalos con `prt reviewers init`."
                : "Capas aplicadas: " + string.Join(" → ", profile.Layers));
            if (repoKey is not null && app.Config.RepoPathFor(repoKey) is null)
                app.Ui.Warn($"{repoKey} no tiene checkout asociado: no se leen sus archivos .prt/ (`prt map {repoKey} <ruta>`).");
            return Task.CompletedTask;
        }));
        return command;
    }

    private static Command InitReviewers()
    {
        var repo = new Argument<string?>("repo") { Description = "workspace/repo (no hace falta con --scope global)", Arity = ArgumentArity.ZeroOrOne };
        var scope = new Option<string>("--scope")
        {
            Description = "repo: .prt/ en el checkout (se versiona y comparte con el equipo); user: solo para vos en este proyecto; global: todos tus proyectos",
            DefaultValueFactory = _ => "repo",
        };
        scope.AcceptOnlyFromAmong("repo", "user", "global");
        var force = new Option<bool>("--force") { Description = "Sobrescribir archivos existentes" };

        var command = new Command("init", "Copia los estándares y revisores incorporados para personalizarlos") { repo, scope, force };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var repoKey = result.GetValue(repo)?.Trim();
            var dir = result.GetValue(scope) switch
            {
                "global" => ProfileLoader.GlobalDir,
                "user" => ProfileLoader.UserProjectDir(repoKey ?? throw new UserException("Indicá el repo (workspace/repo).")),
                _ => Path.Combine(
                    app.Config.RepoPathFor(repoKey ?? throw new UserException("Indicá el repo (workspace/repo)."))
                        ?? throw new UserException($"{repoKey} no tiene checkout asociado. Usá `prt map {repoKey} <ruta>` o --scope user."),
                    ProfileLoader.FolderName),
            };

            var written = ProfileLoader.Scaffold(dir, result.GetValue(force));
            if (written.Count == 0)
                app.Ui.Info($"Ya existían todos los archivos en {dir} (usá --force para sobrescribir).");
            foreach (var path in written)
                app.Ui.Success(path);
            app.Ui.Info("Editá reviewers/*.md (foco y frontmatter), standards.md, context.md y review.json. " +
                        "Podés agregar revisores nuevos creando otro archivo en reviewers/.");
            return Task.CompletedTask;
        }));
        return command;
    }

    private static void RenderRun(PullRequest pr, RunView view)
    {
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(pr.Key)}[/] · panel · {Markup.Escape(view.Run.Verdict ?? "sin veredicto")}").LeftJustified());

        var summary = new Table().Border(TableBorder.Rounded).AddColumns("Revisor", "Agente", "Veredicto", "🔴", "🟠", "🟡", "🔵", "Resueltos");
        foreach (var (result, report) in view.Reviewers)
        {
            if (report is null)
            {
                summary.AddRow(Markup.Escape(result.Reviewer), Markup.Escape(result.Agent),
                    $"[red]{(result.Status == ResultStatus.Invalid ? "formato inválido" : "falló")}[/]", "–", "–", "–", "–", "–");
                continue;
            }
            summary.AddRow(new[] { result.Reviewer, result.Agent, ReviewStandard.Label(report.Verdict) }
                .Concat(Severities.Select(s => report.OpenFindings.Count(f => f.Severity == s).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .Append(report.Findings.Count(f => !f.IsOpen).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Select(Markup.Escape).ToArray());
        }
        AnsiConsole.Write(summary);

        var open = view.OpenFindings.ToList();
        if (open.Count > 0)
        {
            var findings = new Table().Border(TableBorder.Simple).AddColumns("", "ID", "Revisor", "Hallazgo", "Ubicación");
            foreach (var (reviewer, f) in open)
                findings.AddRow(ReviewStandard.Icon(f.Severity), f.Id, Markup.Escape(reviewer), Markup.Escape(f.Title), Markup.Escape(f.Location));
            AnsiConsole.Write(findings);
        }

        foreach (var (reviewer, question) in view.Questions)
            AnsiConsole.MarkupLine($"[blue]?[/] [bold]{Markup.Escape(reviewer)}:[/] {Markup.Escape(question)}");
    }
}
