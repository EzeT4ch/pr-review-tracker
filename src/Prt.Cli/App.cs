using Prt.Core.Agents;
using Prt.Core.Bitbucket;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Export;
using Prt.Core.Panel;
using Prt.Core.Review;
using Prt.Core.Storage;
using Spectre.Console;

namespace Prt.Cli;

/// <summary>Wires configuration, storage and services for one command invocation.</summary>
internal sealed class App : IDisposable
{
    private SqliteStore? _store;
    private HttpClient? _http;

    private App(PrtConfig config) => Config = config;

    public static App Load() => new(ConfigStore.Load());

    public PrtConfig Config { get; }
    public IProcessRunner Runner { get; } = new ProcessRunner();
    public ConsoleUi Ui { get; } = new();

    public SqliteStore Store => _store ??= new SqliteStore(PrtPaths.DatabaseFile);

    // Redirects are followed by BitbucketClient itself, so the Authorization header is kept.
    public IPullRequestSource Bitbucket => new BitbucketClient(
        _http ??= new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) },
        Config.Bitbucket);

    public IAgentAdapter Agent(string name) => AgentFactory.Create(name, Config, Runner);

    public ObsidianExporter? Exporter =>
        string.IsNullOrWhiteSpace(Config.VaultPath)
            ? null
            : new ObsidianExporter(Config.VaultPath, Config.VaultFolder, ResumeCommand);

    public ReviewService Review => new(Config, Store, Bitbucket, Agent, Exporter, Ui);

    public PanelService Panel => new(Config, Store, Bitbucket, Agent, Exporter, Ui, Path.Combine(PrtPaths.DataDir, "runs"));

    public void WarnIfNoVault()
    {
        if (Exporter is null)
            Ui.Warn("No hay vault de Obsidian configurado: no se exportan notas. Configuralo con `prt init`.");
    }

    public string ResumeCommand(string agent, string sessionId)
    {
        try
        {
            return Agent(agent).ResumeCommand(sessionId);
        }
        catch (ArgumentException)
        {
            return $"{agent} {sessionId}";
        }
    }

    /// <summary>A PR already tracked in the database; picks one interactively when no reference is given.</summary>
    public PullRequest RequireTracked(string? text)
    {
        if (text is null)
            return Pick("¿Qué PR?") ?? throw new UserException("No hay PRs registrados todavía. Empezá con `prt review <url>`.");

        var prRef = PrRef.Parse(text);
        return Store.FindPullRequest(prRef)
            ?? throw new UserException($"{prRef.Key} no está registrado. Empezá con `prt review {prRef.Key}`.");
    }

    public PullRequest? Pick(string title)
    {
        var prs = Store.ListPullRequests().Take(30).ToList();
        if (prs.Count == 0)
            return null;
        if (!ConsoleUi.Interactive)
            throw new UserException("Indicá el PR (URL o workspace/repo#numero).");

        return AnsiConsole.Prompt(
            new SelectionPrompt<PullRequest>()
                .Title(title)
                .PageSize(15)
                .UseConverter(p => Markup.Escape(
                    $"{p.Key}  {Truncate(p.Title, 60)}  [{(string.IsNullOrEmpty(p.Verdict) ? p.State : p.Verdict)}]"))
                .AddChoices(prs));
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public void Dispose()
    {
        _store?.Dispose();
        _http?.Dispose();
    }
}

/// <summary>An error caused by user input or setup; shown without a stack trace.</summary>
internal sealed class UserException(string message) : Exception(message);
