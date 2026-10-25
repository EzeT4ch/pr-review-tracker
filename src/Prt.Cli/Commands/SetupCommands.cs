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
        var path = new Argument<string>("path") { Description = "Checkout local donde corre el agente" };
        var command = new Command("map", "Asocia un repo de Bitbucket a un checkout local") { repo, path };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var full = Path.GetFullPath(result.GetValue(path)!);
            if (!Directory.Exists(full))
                throw new UserException($"{full} no existe.");
            app.Config.RepoPaths[result.GetValue(repo)!.Trim()] = full;
            ConfigStore.Save(app.Config);
            app.Ui.Success($"{result.GetValue(repo)} → {full}");
            return Task.CompletedTask;
        }));
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
