using System.CommandLine;
using Prt.Core.Config;
using Spectre.Console;

namespace Prt.Cli.Commands;

internal static class SetupCommands
{
    public static Command Init()
    {
        var vault = new Option<string?>("--vault") { Description = "Ruta al vault de Obsidian" };
        var folder = new Option<string?>("--folder") { Description = "Carpeta dentro del vault (por defecto 'PR Reviews')" };
        var agent = ReviewCommands.AgentOption();
        var user = new Option<string?>("--bb-user") { Description = "Email de Atlassian (API token) o usuario de Bitbucket (app password)" };
        var token = new Option<string?>("--bb-token") { Description = "API token / app password de Bitbucket con permiso de lectura de PRs" };

        var command = new Command("init", "Configura vault, credenciales de Bitbucket y agente por defecto")
        {
            vault, folder, agent, user, token,
        };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var config = app.Config;
            var interactive = ConsoleUi.Interactive;

            config.VaultPath = result.GetValue(vault) is { } v ? Path.GetFullPath(v)
                : interactive ? Ask("Ruta del vault de Obsidian", config.VaultPath) is { Length: > 0 } a ? Path.GetFullPath(a) : null
                : config.VaultPath;
            config.VaultFolder = result.GetValue(folder) ?? (interactive ? Ask("Carpeta dentro del vault", config.VaultFolder) : null) ?? config.VaultFolder;
            config.DefaultAgent = result.GetValue(agent) ?? (interactive
                ? AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Agente por defecto").AddChoices(
                    AgentNames.All.OrderBy(n => n == config.DefaultAgent ? 0 : 1)))
                : config.DefaultAgent);
            config.Bitbucket.Username = result.GetValue(user)
                ?? (interactive ? Ask("Bitbucket: email de Atlassian o usuario", config.Bitbucket.Username) : null)
                ?? config.Bitbucket.Username;
            config.Bitbucket.ApiToken = result.GetValue(token)
                ?? (interactive ? AskSecret(config.Bitbucket.ApiToken is null) : null)
                ?? config.Bitbucket.ApiToken;

            if (config.VaultPath is not null && !Directory.Exists(config.VaultPath))
                app.Ui.Warn($"El vault {config.VaultPath} no existe todavía; se creará la carpeta al exportar.");

            while (interactive && AnsiConsole.Confirm("¿Asociar un repo de Bitbucket a un checkout local? (directorio donde corre el agente)", false))
            {
                var repo = AnsiConsole.Ask<string>("Repo (workspace/repo):").Trim();
                var path = Path.GetFullPath(AnsiConsole.Ask<string>("Ruta local:").Trim());
                config.RepoPaths[repo] = path;
            }

            ConfigStore.Save(config);
            app.Ui.Success($"Configuración guardada en {PrtPaths.ConfigFile}");
            app.Ui.Info($"Base de datos: {PrtPaths.DatabaseFile}");
            return Task.CompletedTask;
        }));
        return command;
    }

    public static Command Map()
    {
        var repo = new Argument<string>("repo") { Description = "workspace/repo" };
        var path = new Argument<string?>("path") { Description = "Checkout local donde corren los agentes", Arity = ArgumentArity.ZeroOrOne };
        var managed = new Option<bool>("--managed") { Description = "Quitar el checkout local y usar el clon gestionado por prt" };
        var command = new Command("map", "Asocia un repo de Bitbucket a un checkout local (o vuelve al clon gestionado)") { repo, path, managed };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var repoKey = result.GetValue(repo)!.Trim();
            if (result.GetValue(managed))
            {
                app.Config.RepoPaths.Remove(repoKey);
                ConfigStore.Save(app.Config);
                app.Ui.Success(app.Config.ManagedClones
                    ? $"{repoKey} usa el clon gestionado por prt (en la rama de destino de cada PR)."
                    : $"{repoKey} ya no tiene checkout local; los clones gestionados están desactivados (managedClones en el config).");
                return Task.CompletedTask;
            }

            var full = Path.GetFullPath(result.GetValue(path) ?? throw new UserException("Indicá la ruta del checkout, o --managed."));
            if (!Directory.Exists(full))
                throw new UserException($"{full} no existe.");
            app.Config.RepoPaths[repoKey] = full;
            ConfigStore.Save(app.Config);
            app.Ui.Success($"{repoKey} → {full}");
            return Task.CompletedTask;
        }));
        return command;
    }

    public static Command Repos()
    {
        var command = new Command("repos", "Muestra de dónde leen el código los agentes en cada repo y el tamaño de los clones gestionados");
        command.SetAction((_, _) => Cli.Run(app =>
        {
            var managed = app.Managed;
            var repos = app.Store.ListPullRequests().Select(p => p.Ref.RepoKey).Concat(app.Config.RepoPaths.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (repos.Count == 0)
            {
                app.Ui.Info("Todavía no hay repos registrados.");
                return Task.CompletedTask;
            }

            var table = new Table().Border(Symbols.Border).AddColumns("Repo", "Código para los agentes", "Clon gestionado");
            foreach (var repoKey in repos)
            {
                var source = app.Config.RepoPathFor(repoKey) is { } local ? $"checkout local {local}"
                    : app.Config.RepoPaths.TryGetValue(repoKey, out var missing) ? $"checkout {missing} (no existe)"
                    : app.Config.ManagedClones ? "clon gestionado" : "solo el diff";
                var clone = managed.Exists(repoKey)
                    ? $"{managed.Size(repoKey)} · {string.Join(", ", managed.Worktrees(repoKey))}"
                    : "—";
                table.AddRow(Markup.Escape(repoKey), Markup.Escape(source), Markup.Escape(clone));
            }
            AnsiConsole.Write(table);
            app.Ui.Info($"Clones en {managed.Root}");
            return Task.CompletedTask;
        }));

        var repoArg = new Argument<string?>("repo") { Description = "workspace/repo (por defecto, todos)", Arity = ArgumentArity.ZeroOrOne };
        var clean = new Command("clean", "Borra clones gestionados (se vuelven a crear en el próximo análisis)") { repoArg };
        clean.SetAction((result, _) => Cli.Run(app =>
        {
            var managed = app.Managed;
            if (result.GetValue(repoArg) is { } one)
            {
                managed.Delete(one.Trim());
                app.Ui.Success($"Clon gestionado de {one} borrado.");
            }
            else if (Directory.Exists(managed.Root))
            {
                foreach (var workspace in Directory.GetDirectories(managed.Root))
                    foreach (var repoDir in Directory.GetDirectories(workspace))
                        managed.Delete($"{Path.GetFileName(workspace)}/{Path.GetFileName(repoDir)}");
                app.Ui.Success("Clones gestionados borrados.");
            }
            return Task.CompletedTask;
        }));
        command.Subcommands.Add(clean);
        return command;
    }

    private static string? Ask(string question, string? current)
    {
        var prompt = new TextPrompt<string>(Markup.Escape(question) + ":").AllowEmpty();
        if (!string.IsNullOrEmpty(current))
            prompt.DefaultValue(current);
        var answer = AnsiConsole.Prompt(prompt).Trim();
        return answer.Length == 0 ? current : answer;
    }

    private static string? AskSecret(bool missing)
    {
        var label = missing ? "Bitbucket: API token" : "Bitbucket: API token (vacío para conservar el actual)";
        var answer = AnsiConsole.Prompt(new TextPrompt<string>(label + ":").Secret().AllowEmpty()).Trim();
        return answer.Length == 0 ? null : answer;
    }
}
