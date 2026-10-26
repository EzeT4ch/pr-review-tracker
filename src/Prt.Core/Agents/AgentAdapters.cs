using System.Text.Json;
using System.Text.RegularExpressions;
using Prt.Core.Config;

namespace Prt.Core.Agents;

public interface IAgentAdapter
{
    string Name { get; }

    /// <summary>Starts a new interactive session; returns its id, or null when it could not be detected.</summary>
    Task<string?> StartAsync(string prompt, string cwd, CancellationToken ct = default);

    /// <summary>Re-opens an existing session interactively, optionally sending a first message.</summary>
    Task ResumeAsync(string sessionId, string cwd, string? prompt, CancellationToken ct = default);

    /// <summary>Sends one message to the session headless and returns the agent's final answer.</summary>
    Task<string> AskAsync(string sessionId, string cwd, string prompt, CancellationToken ct = default);

    /// <summary>
    /// Runs a panel reviewer headless and read-only: starts a session when <paramref name="sessionId"/> is null,
    /// otherwise continues it. Returns the session id (null if the agent did not report it) and the answer.
    /// </summary>
    /// <param name="readDirs">Extra folders the reviewer must be able to read (the repos of a PR group).</param>
    Task<HeadlessResult> RunReviewAsync(string? sessionId, string prompt, string cwd, string contextDir, CancellationToken ct = default,
        IReadOnlyList<string>? readDirs = null);

    /// <summary>Command a human can paste to reopen the session without prt.</summary>
    string ResumeCommand(string sessionId);
}

public sealed record HeadlessResult(string? SessionId, string Answer);

public sealed class AgentException(string message) : Exception(message);

public static class AgentFactory
{
    public static IAgentAdapter Create(string name, PrtConfig config, IProcessRunner runner) =>
        name.ToLowerInvariant() switch
        {
            AgentNames.Claude => new ClaudeAdapter(config.GetAgent(name), runner),
            AgentNames.Codex => new CodexAdapter(config.GetAgent(name), runner),
            AgentNames.Opencode => new OpencodeAdapter(config.GetAgent(name), runner),
            _ => throw new ArgumentException($"Agente desconocido '{name}'. Opciones: {string.Join(", ", AgentNames.All)}."),
        };
}

public abstract class AgentAdapterBase(AgentSettings settings, IProcessRunner runner) : IAgentAdapter
{
    protected AgentSettings Settings { get; } = settings;
    protected IProcessRunner Runner { get; } = runner;
    protected string Command => Settings.Command!;

    public abstract string Name { get; }

    public async Task<string?> StartAsync(string prompt, string cwd, CancellationToken ct = default)
    {
        var presetId = PresetSessionId();
        var snapshot = presetId is null ? await SnapshotAsync(cwd, ct) : null;
        await Runner.RunInteractiveAsync(Command, Expand(Settings.NewArgs!, presetId, prompt), cwd, ct);
        return presetId ?? await DetectNewSessionAsync(snapshot, cwd, ct);
    }

    public Task ResumeAsync(string sessionId, string cwd, string? prompt, CancellationToken ct = default)
    {
        var template = prompt is null ? Settings.ResumeArgs! : Settings.ResumeWithPromptArgs!;
        return Runner.RunInteractiveAsync(Command, Expand(template, sessionId, prompt), cwd, ct);
    }

    public async Task<string> AskAsync(string sessionId, string cwd, string prompt, CancellationToken ct = default) =>
        (await RunHeadlessAsync(Expand(Settings.AskArgs!, sessionId, prompt), cwd, ct)).Answer;

    public async Task<HeadlessResult> RunReviewAsync(string? sessionId, string prompt, string cwd, string contextDir, CancellationToken ct = default,
        IReadOnlyList<string>? readDirs = null)
    {
        var id = sessionId ?? PresetSessionId();
        var template = sessionId is null ? Settings.ReviewNewArgs! : Settings.ReviewResumeArgs!;
        var (answer, stdout) = await RunHeadlessAsync(Expand(template, id, prompt, contextDir, readDirs), cwd, ct);
        return new HeadlessResult(id ?? ExtractSessionId(stdout), answer);
    }

    private async Task<(string Answer, string StdOut)> RunHeadlessAsync(List<string> args, string cwd, CancellationToken ct)
    {
        var result = await Runner.RunCapturedAsync(Command, args, cwd, ct);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            throw new AgentException($"{Name} terminó con código {result.ExitCode}: {Truncate(detail.Trim(), 600)}");
        }

        var answer = ParseAnswer(result.StdOut).Trim();
        if (answer.Length == 0)
            throw new AgentException($"{Name} no devolvió texto.");
        return (answer, result.StdOut);
    }

    public string ResumeCommand(string sessionId) =>
        string.Join(' ', new[] { Command }.Concat(Expand(Settings.ResumeArgs!, sessionId, null)).Select(QuoteForShell));

    /// <summary>Session id chosen before launching, for agents that accept one.</summary>
    protected virtual string? PresetSessionId() => null;

    protected virtual Task<object?> SnapshotAsync(string cwd, CancellationToken ct) => Task.FromResult<object?>(null);

    protected virtual Task<string?> DetectNewSessionAsync(object? snapshot, string cwd, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    /// <summary>Extracts the final answer from the headless output.</summary>
    protected abstract string ParseAnswer(string stdout);

    /// <summary>Session id reported in the headless output of a new session.</summary>
    protected virtual string? ExtractSessionId(string stdout) => null;

    internal static List<string> Expand(IEnumerable<string> template, string? sessionId, string? prompt, string? contextDir = null,
        IReadOnlyList<string>? readDirs = null)
    {
        var args = new List<string>();
        foreach (var arg in template)
        {
            if (arg == "{prompt}" && prompt is null)
                continue;
            // A whole argument: one argument per folder, or none.
            if (arg == ReadDirsPlaceholder)
            {
                args.AddRange(readDirs ?? []);
                continue;
            }
            args.Add(arg.Replace("{sessionId}", sessionId ?? "").Replace("{contextDir}", contextDir ?? "").Replace("{prompt}", prompt ?? ""));
        }
        return args;
    }

    public const string ReadDirsPlaceholder = "{readDirs}";

    /// <summary>Whether this agent's review templates can grant read access to extra folders.</summary>
    public bool SupportsReadDirs =>
        Settings.ReviewNewArgs!.Contains(ReadDirsPlaceholder) && Settings.ReviewResumeArgs!.Contains(ReadDirsPlaceholder);

    internal static IEnumerable<JsonElement> JsonLines(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{'))
                continue;
            JsonElement element;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                element = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }
            yield return element;
        }
    }

    internal static string? Str(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static string QuoteForShell(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./:=@".Contains(c)) ? arg : $"\"{arg.Replace("\"", "\\\"")}\"";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>Claude Code: the session id is chosen up front with --session-id.</summary>
public sealed class ClaudeAdapter(AgentSettings settings, IProcessRunner runner) : AgentAdapterBase(settings, runner)
{
    public override string Name => AgentNames.Claude;

    protected override string? PresetSessionId() => Guid.NewGuid().ToString();

    // `claude -p --output-format json` prints a single object: { "result": "...", "is_error": false, ... }
    protected override string ParseAnswer(string stdout)
    {
        foreach (var json in JsonLines(stdout).Reverse())
        {
            if (json.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                throw new AgentException($"claude devolvió un error: {Str(json, "result") ?? stdout.Trim()}");
            if (Str(json, "result") is { } result)
                return result;
        }
        return stdout;
    }
}

/// <summary>Codex CLI: the new session is found in the rollout files under $CODEX_HOME/sessions.</summary>
public sealed class CodexAdapter(AgentSettings settings, IProcessRunner runner, string? codexHome = null)
    : AgentAdapterBase(settings, runner)
{
    private readonly string _sessionsDir = Path.Combine(
        codexHome
        ?? (Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home ? home : null)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
        "sessions");

    public override string Name => AgentNames.Codex;

    protected override Task<object?> SnapshotAsync(string cwd, CancellationToken ct) =>
        Task.FromResult<object?>(RolloutFiles().ToHashSet(StringComparer.Ordinal));

    protected override Task<string?> DetectNewSessionAsync(object? snapshot, string cwd, CancellationToken ct)
    {
        var before = (HashSet<string>)snapshot!;
        var candidates = RolloutFiles()
            .Where(f => !before.Contains(f))
            .Select(f => (File: f, Meta: ReadMeta(f)))
            .Where(c => c.Meta.Id is not null)
            .OrderByDescending(c => File.GetLastWriteTimeUtc(c.File))
            .ToList();

        var match = candidates.FirstOrDefault(c => SamePath(c.Meta.Cwd, cwd));
        if (match.File is null && candidates.Count > 0)
            match = candidates[0];
        return Task.FromResult(match.Meta.Id);
    }

    // `codex exec --json` emits JSONL events; keep the last agent message (new and legacy shapes).
    protected override string ParseAnswer(string stdout)
    {
        string? last = null;
        foreach (var json in JsonLines(stdout))
        {
            if (Str(json, "type") == "item.completed" && Str(json, "item", "type") == "agent_message")
                last = Str(json, "item", "text") ?? last;
            else if (Str(json, "msg", "type") == "agent_message")
                last = Str(json, "msg", "message") ?? last;
        }
        return last ?? stdout;
    }

    // New format: {"type":"thread.started","thread_id":..}; legacy: {"msg":{"type":"session_configured","session_id":..}}
    protected override string? ExtractSessionId(string stdout)
    {
        foreach (var json in JsonLines(stdout))
        {
            if (Str(json, "type") == "thread.started" && Str(json, "thread_id") is { Length: > 0 } thread)
                return thread;
            if (Str(json, "msg", "type") == "session_configured" && Str(json, "msg", "session_id") is { Length: > 0 } session)
                return session;
            if (Str(json, "type") == "session_meta" && Str(json, "payload", "id") is { Length: > 0 } meta)
                return meta;
        }
        return null;
    }

    private IEnumerable<string> RolloutFiles() =>
        Directory.Exists(_sessionsDir)
            ? Directory.EnumerateFiles(_sessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)
            : [];

    private static readonly Regex UuidPattern =
        new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);

    internal static (string? Id, string? Cwd) ReadMeta(string file)
    {
        string? firstLine;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            firstLine = reader.ReadLine();
        }
        catch (IOException)
        {
            firstLine = null;
        }

        var json = firstLine is null ? (JsonElement?)null : JsonLines(firstLine).Cast<JsonElement?>().FirstOrDefault();
        // Current format: {"type":"session_meta","payload":{"id":..,"cwd":..}}; legacy: {"id":..}
        var id = json is { } j ? Str(j, "payload", "id") ?? Str(j, "id") : null;
        var cwd = json is { } k ? Str(k, "payload", "cwd") ?? Str(k, "cwd") : null;
        id ??= UuidPattern.Matches(Path.GetFileNameWithoutExtension(file)).LastOrDefault()?.Value;
        return (id, cwd);
    }

    private static bool SamePath(string? a, string b)
    {
        if (string.IsNullOrEmpty(a))
            return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), comparison);
    }
}

/// <summary>opencode: the new session is found by diffing `opencode session list` before and after.</summary>
public sealed class OpencodeAdapter(AgentSettings settings, IProcessRunner runner) : AgentAdapterBase(settings, runner)
{
    private static readonly Regex SessionIdPattern = new(@"\bses_[A-Za-z0-9]+\b");

    public override string Name => AgentNames.Opencode;

    protected override async Task<object?> SnapshotAsync(string cwd, CancellationToken ct) =>
        (await ListSessionsAsync(cwd, ct)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

    protected override async Task<string?> DetectNewSessionAsync(object? snapshot, string cwd, CancellationToken ct)
    {
        var before = (HashSet<string>)snapshot!;
        return (await ListSessionsAsync(cwd, ct))
            .Where(s => !before.Contains(s.Id))
            .OrderByDescending(s => s.Updated)
            .Select(s => s.Id)
            .FirstOrDefault();
    }

    // `opencode run --format json` emits JSONL events; text parts carry the answer.
    protected override string ParseAnswer(string stdout)
    {
        string? last = null;
        foreach (var json in JsonLines(stdout))
        {
            if (Str(json, "type") == "text" && Str(json, "part", "text") is { Length: > 0 } text)
                last = text;
        }
        return last ?? stdout;
    }

    // Every event of `opencode run --format json` carries the session id.
    protected override string? ExtractSessionId(string stdout) =>
        JsonLines(stdout).Select(j => Str(j, "sessionID") ?? Str(j, "part", "sessionID")).FirstOrDefault(id => !string.IsNullOrEmpty(id));

    private async Task<List<(string Id, double Updated)>> ListSessionsAsync(string cwd, CancellationToken ct)
    {
        ProcessResult result;
        try
        {
            result = await Runner.RunCapturedAsync(Command, ["session", "list", "--format", "json"], cwd, ct);
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        return result.ExitCode == 0 ? ParseSessionList(result.StdOut) : [];
    }

    internal static List<(string Id, double Updated)> ParseSessionList(string output)
    {
        var sessions = new List<(string, double)>();
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var order = 0;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (Str(item, "id") is not { } id)
                        continue;
                    // Unknown shapes keep list order (newest first) via a decreasing fallback.
                    var updated = Number(item, "updated") ?? Number(item, "time", "updated") ?? -order;
                    sessions.Add((id, updated));
                    order++;
                }
                return sessions;
            }
        }
        catch (JsonException)
        {
            // Plain-text table: fall back to scraping ids.
        }

        var index = 0;
        foreach (Match match in SessionIdPattern.Matches(output))
            sessions.Add((match.Value, -index++));
        return sessions;
    }

    private static double? Number(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                return null;
        }
        if (element.ValueKind == JsonValueKind.Number)
            return element.GetDouble();
        if (element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(element.GetString(), out var date))
            return date.ToUnixTimeMilliseconds();
        return null;
    }
}
