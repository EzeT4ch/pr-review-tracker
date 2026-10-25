using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prt.Core.Config;

public sealed class PrtConfig
{
    /// <summary>Root folder of the Obsidian vault.</summary>
    public string? VaultPath { get; set; }

    /// <summary>Folder inside the vault where review notes are written.</summary>
    public string VaultFolder { get; set; } = "PR Reviews";

    public string DefaultAgent { get; set; } = AgentNames.Claude;

    public BitbucketSettings Bitbucket { get; set; } = new();

    /// <summary>"workspace/repo" → local checkout used as the agent's working directory.</summary>
    public Dictionary<string, string> RepoPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-agent overrides of the command and argument templates.</summary>
    public Dictionary<string, AgentSettings> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public PromptSettings Prompts { get; set; } = new();

    public AgentSettings GetAgent(string name)
    {
        var defaults = AgentSettings.DefaultFor(name);
        if (!Agents.TryGetValue(name, out var custom))
            return defaults;

        return new AgentSettings
        {
            Command = custom.Command ?? defaults.Command,
            NewArgs = custom.NewArgs ?? defaults.NewArgs,
            ResumeArgs = custom.ResumeArgs ?? defaults.ResumeArgs,
            ResumeWithPromptArgs = custom.ResumeWithPromptArgs ?? defaults.ResumeWithPromptArgs,
            AskArgs = custom.AskArgs ?? defaults.AskArgs,
        };
    }

    public string? RepoPathFor(string repoKey) =>
        RepoPaths.TryGetValue(repoKey, out var path) && Directory.Exists(path) ? path : null;
}

public sealed class BitbucketSettings
{
    public string BaseUrl { get; set; } = "https://api.bitbucket.org/2.0";

    /// <summary>Atlassian account email (API tokens) or Bitbucket username (app passwords).</summary>
    public string? Username { get; set; }

    public string? ApiToken { get; set; }

    [JsonIgnore]
    public string? EffectiveUsername => Environment.GetEnvironmentVariable("PRT_BB_USER") is { Length: > 0 } v ? v : Username;

    [JsonIgnore]
    public string? EffectiveToken => Environment.GetEnvironmentVariable("PRT_BB_TOKEN") is { Length: > 0 } v ? v : ApiToken;
}

public static class AgentNames
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Opencode = "opencode";

    public static readonly string[] All = [Claude, Codex, Opencode];
}

/// <summary>
/// Command line templates for an agent CLI. Arguments may contain the placeholders
/// <c>{sessionId}</c> and <c>{prompt}</c>. Agent CLIs change often, so every template
/// can be overridden in the config file.
/// </summary>
public sealed class AgentSettings
{
    public string? Command { get; set; }
    public List<string>? NewArgs { get; set; }
    public List<string>? ResumeArgs { get; set; }
    public List<string>? ResumeWithPromptArgs { get; set; }
    public List<string>? AskArgs { get; set; }

    public static AgentSettings DefaultFor(string name) => name.ToLowerInvariant() switch
    {
        AgentNames.Claude => new()
        {
            Command = "claude",
            NewArgs = ["--session-id", "{sessionId}", "{prompt}"],
            ResumeArgs = ["--resume", "{sessionId}"],
            ResumeWithPromptArgs = ["--resume", "{sessionId}", "{prompt}"],
            AskArgs = ["-p", "--resume", "{sessionId}", "--output-format", "json", "{prompt}"],
        },
        AgentNames.Codex => new()
        {
            Command = "codex",
            NewArgs = ["{prompt}"],
            ResumeArgs = ["resume", "{sessionId}"],
            ResumeWithPromptArgs = ["resume", "{sessionId}", "{prompt}"],
            AskArgs = ["exec", "resume", "{sessionId}", "--json", "{prompt}"],
        },
        AgentNames.Opencode => new()
        {
            Command = "opencode",
            NewArgs = ["--prompt", "{prompt}"],
            ResumeArgs = ["--session", "{sessionId}"],
            ResumeWithPromptArgs = ["--session", "{sessionId}", "--prompt", "{prompt}"],
            AskArgs = ["run", "--session", "{sessionId}", "--format", "json", "{prompt}"],
        },
        _ => throw new ArgumentException(
            $"Agente desconocido '{name}'. Opciones: {string.Join(", ", AgentNames.All)}."),
    };
}

public sealed class PromptSettings
{
    public string Initial { get; set; } = DefaultPrompts.Initial;
    public string ReReview { get; set; } = DefaultPrompts.ReReview;
    public string Summary { get; set; } = DefaultPrompts.Summary;
}

public static class PrtPaths
{
    public static string ConfigDir =>
        Environment.GetEnvironmentVariable("PRT_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "prt");

    public static string DataDir =>
        Environment.GetEnvironmentVariable("PRT_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "prt");

    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public static string DatabaseFile => Path.Combine(DataDir, "prt.db");
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PrtConfig Load(string? path = null)
    {
        path ??= PrtPaths.ConfigFile;
        if (!File.Exists(path))
            return new PrtConfig();

        var config = JsonSerializer.Deserialize<PrtConfig>(File.ReadAllText(path), Options) ?? new PrtConfig();
        // Deserialization replaces the dictionaries, losing the case-insensitive comparer.
        config.RepoPaths = new(config.RepoPaths, StringComparer.OrdinalIgnoreCase);
        config.Agents = new(config.Agents, StringComparer.OrdinalIgnoreCase);
        return config;
    }

    public static void Save(PrtConfig config, string? path = null)
    {
        path ??= PrtPaths.ConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
        // The file may hold the Bitbucket token.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
