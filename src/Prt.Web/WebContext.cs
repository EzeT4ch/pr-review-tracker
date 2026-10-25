using Prt.Core.Agents;
using Prt.Core.Bitbucket;
using Prt.Core.Config;
using Prt.Core.Export;
using Prt.Core.Repos;
using Prt.Core.Storage;

namespace Prt.Web;

/// <summary>What the web panel needs from the rest of prt. Built by the CLI, faked in tests.</summary>
public sealed class WebContext
{
    /// <summary>Read on every request, so `prt map`, `prt init`… apply without restarting the server.</summary>
    public required Func<PrtConfig> LoadConfig { get; init; }

    /// <summary>Persists config changes made from the panel (checkout mappings).</summary>
    public required Action<PrtConfig> SaveConfig { get; init; }

    public required string DatabasePath { get; init; }
    public required string RunsRoot { get; init; }
    public required string ReposRoot { get; init; }
    public required Func<PrtConfig, IPullRequestSource> Bitbucket { get; init; }
    public required Func<PrtConfig, string, IAgentAdapter> Agent { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>One connection per request or job: the store is not thread-safe and holds no cache.</summary>
    public SqliteStore OpenStore() => new(DatabasePath, Clock);

    public ManagedRepos Managed(PrtConfig config) => new(ReposRoot, config.Bitbucket);

    public ICheckoutProvider Checkouts(PrtConfig config) => new CheckoutResolver(config, Managed(config));

    public ObsidianExporter? Exporter(PrtConfig config) =>
        string.IsNullOrWhiteSpace(config.VaultPath)
            ? null
            : new ObsidianExporter(config.VaultPath, config.VaultFolder, (agent, id) => ResumeCommand(config, agent, id));

    public string ResumeCommand(PrtConfig config, string agent, string sessionId)
    {
        try
        {
            return Agent(config, agent).ResumeCommand(sessionId);
        }
        catch (ArgumentException)
        {
            return $"{agent} {sessionId}";
        }
    }
}

public sealed record WebOptions(int Port, TimeSpan SleepAfter, string Token);
