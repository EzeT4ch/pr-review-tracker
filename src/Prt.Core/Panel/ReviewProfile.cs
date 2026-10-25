using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prt.Core.Config;

namespace Prt.Core.Panel;

/// <summary>One specialised reviewer of the panel, defined by a Markdown file with frontmatter.</summary>
public sealed record ReviewerDefinition
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string IdPrefix { get; init; }
    public required string Instructions { get; init; }
    public bool Enabled { get; init; } = true;
    public int Order { get; init; } = 100;

    /// <summary>Agent for this reviewer; null uses the project or global default.</summary>
    public string? Agent { get; init; }

    /// <summary>Where the definition came from (a path, or "incorporado").</summary>
    public string Source { get; init; } = ProfileLoader.BuiltInSource;
}

/// <summary>project/.prt/review.json</summary>
public sealed class ProjectReviewSettings
{
    /// <summary>Reviewers to run, in order. Null runs every enabled reviewer.</summary>
    public List<string>? Reviewers { get; set; }

    public string? DefaultAgent { get; set; }

    /// <summary>reviewer → agent, overriding the reviewer files.</summary>
    public Dictionary<string, string>? Agents { get; set; }

    public int? MaxParallel { get; set; }

    /// <summary>Repo-relative documents every reviewer should read (ADRs, architecture docs…).</summary>
    public List<string>? ContextFiles { get; set; }
}

/// <summary>Everything that defines how PRs of one project are reviewed, after merging all layers.</summary>
public sealed record ReviewProfile(
    IReadOnlyList<ReviewerDefinition> Reviewers,
    string Standards,
    string Context,
    IReadOnlyList<string> ContextFiles,
    string? DefaultAgent,
    int MaxParallel,
    IReadOnlyList<string> Layers)
{
    /// <summary>Reviewers to run: the requested names, or the enabled ones.</summary>
    public IReadOnlyList<ReviewerDefinition> Select(IReadOnlyCollection<string>? names)
    {
        if (names is null || names.Count == 0)
            return Reviewers.Where(r => r.Enabled).ToList();

        var byName = Reviewers.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var unknown = names.Where(n => !byName.ContainsKey(n)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException(
                $"Revisor(es) desconocido(s): {string.Join(", ", unknown)}. Disponibles: {string.Join(", ", Reviewers.Select(r => r.Name))}.");
        return names.Select(n => byName[n]).ToList();
    }
}

/// <summary>
/// Resolves the review profile of a project by layering, from lowest to highest priority:
/// built-in defaults, the global folder (~/.config/prt), the repo's <c>.prt/</c> folder (shared with the team)
/// and the user's per-project folder (~/.config/prt/projects/&lt;workspace&gt;/&lt;repo&gt;).
/// A reviewer file replaces the one with the same name; standards.md replaces; context.md accumulates;
/// review.json merges property by property.
/// </summary>
public static class ProfileLoader
{
    public const string BuiltInSource = "incorporado";
    public const string FolderName = ".prt";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GlobalDir => PrtPaths.ConfigDir;

    public static string UserProjectDir(string repoKey) =>
        Path.Combine(PrtPaths.ConfigDir, "projects", Path.Combine(repoKey.Split('/')));

    public static string? RepoDir(string? checkout) => checkout is null ? null : Path.Combine(checkout, FolderName);

    /// <summary>Layer folders, lowest priority first (they may not exist).</summary>
    public static IReadOnlyList<string> LayerDirs(string repoKey, string? checkout)
    {
        var dirs = new List<string> { GlobalDir };
        if (RepoDir(checkout) is { } repo)
            dirs.Add(repo);
        dirs.Add(UserProjectDir(repoKey));
        return dirs;
    }

    public static ReviewProfile Load(string repoKey, string? checkout) => Load(LayerDirs(repoKey, checkout));

    public static ReviewProfile Load(IReadOnlyList<string> layerDirs)
    {
        var reviewers = new Dictionary<string, ReviewerDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, text) in BuiltIn.Reviewers)
            reviewers[name] = ParseReviewer(text, BuiltInSource, name);

        var standards = BuiltIn.Standards;
        var contexts = new List<string>();
        var settings = new ProjectReviewSettings();
        var usedLayers = new List<string>();

        foreach (var dir in layerDirs.Where(Directory.Exists))
        {
            var used = false;
            var reviewersDir = Path.Combine(dir, "reviewers");
            if (Directory.Exists(reviewersDir))
            {
                foreach (var file in Directory.EnumerateFiles(reviewersDir, "*.md").Order(StringComparer.Ordinal))
                {
                    var definition = ParseReviewer(File.ReadAllText(file), file, Path.GetFileNameWithoutExtension(file));
                    reviewers[definition.Name] = definition;
                    used = true;
                }
            }

            if (ReadIfExists(Path.Combine(dir, "standards.md")) is { } s)
            {
                standards = s;
                used = true;
            }

            if (ReadIfExists(Path.Combine(dir, "context.md")) is { } c && HasContent(c))
            {
                contexts.Add(c.Trim());
                used = true;
            }

            if (ReadIfExists(Path.Combine(dir, "review.json")) is { } json)
            {
                Merge(settings, ParseSettings(json, Path.Combine(dir, "review.json")));
                used = true;
            }

            if (used)
                usedLayers.Add(dir);
        }

        if (settings.Agents is not null)
        {
            foreach (var (name, agent) in settings.Agents)
            {
                if (reviewers.TryGetValue(name, out var r))
                    reviewers[name] = r with { Agent = agent };
            }
        }

        var ordered = reviewers.Values.OrderBy(r => r.Order).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        if (settings.Reviewers is { } selected)
        {
            var set = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = selected.Where(n => !reviewers.ContainsKey(n)).ToList();
            if (missing.Count > 0)
                throw new ArgumentException($"review.json lista revisores que no existen: {string.Join(", ", missing)}.");
            // The list decides both which run by default and their order.
            ordered = selected.Select(n => reviewers[n] with { Enabled = true })
                .Concat(ordered.Where(r => !set.Contains(r.Name)).Select(r => r with { Enabled = false }))
                .ToList();
        }

        foreach (var agent in ordered.Select(r => r.Agent).Append(settings.DefaultAgent).OfType<string>())
        {
            if (!AgentNames.All.Contains(agent, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Agente desconocido '{agent}' en la configuración de revisores. Opciones: {string.Join(", ", AgentNames.All)}.");
        }

        return new ReviewProfile(
            ordered,
            standards.Trim(),
            string.Join("\n\n", contexts),
            settings.ContextFiles ?? [],
            settings.DefaultAgent,
            Math.Clamp(settings.MaxParallel ?? 4, 1, 16),
            usedLayers);
    }

    /// <summary>Copies the built-in standards, context template, settings and reviewers into a layer folder.</summary>
    public static IReadOnlyList<string> Scaffold(string dir, bool overwrite)
    {
        var written = new List<string>();
        void Write(string relative, string content)
        {
            var path = Path.Combine(dir, relative);
            if (File.Exists(path) && !overwrite)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            written.Add(path);
        }

        Write("standards.md", BuiltIn.Standards);
        Write("context.md", BuiltIn.Context);
        Write("review.json", JsonSerializer.Serialize(new ProjectReviewSettings
        {
            Reviewers = BuiltIn.Reviewers.Select(r => ParseReviewer(r.Text, BuiltInSource, r.Name)).OrderBy(r => r.Order).Select(r => r.Name).ToList(),
            DefaultAgent = null,
            Agents = new Dictionary<string, string>(),
            MaxParallel = 4,
            ContextFiles = [],
        }, JsonOptions) + "\n");
        foreach (var (name, text) in BuiltIn.Reviewers)
            Write(Path.Combine("reviewers", name + ".md"), text);
        return written;
    }

    internal static ReviewerDefinition ParseReviewer(string text, string source, string fallbackName)
    {
        var (meta, body) = Frontmatter.Split(text);
        string? Get(string key) => meta.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        var name = (Get("name") ?? fallbackName).Trim().ToLowerInvariant();
        if (body.Trim().Length == 0)
            throw new ArgumentException($"El revisor '{name}' ({source}) no tiene instrucciones.");

        return new ReviewerDefinition
        {
            Name = name,
            Title = Get("title") ?? name,
            IdPrefix = (Get("id_prefix") ?? new string(name.Where(char.IsLetter).Take(4).ToArray())).ToUpperInvariant(),
            Instructions = body.Trim(),
            Enabled = Get("enabled") is not { } enabled || !enabled.Equals("false", StringComparison.OrdinalIgnoreCase),
            Order = int.TryParse(Get("order"), out var order) ? order : 100,
            Agent = Get("agent")?.ToLowerInvariant(),
            Source = source,
        };
    }

    private static ProjectReviewSettings ParseSettings(string json, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ProjectReviewSettings>(json, JsonOptions) ?? new ProjectReviewSettings();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{path} no es JSON válido: {ex.Message}");
        }
    }

    private static void Merge(ProjectReviewSettings target, ProjectReviewSettings layer)
    {
        target.Reviewers = layer.Reviewers ?? target.Reviewers;
        target.DefaultAgent = layer.DefaultAgent ?? target.DefaultAgent;
        target.MaxParallel = layer.MaxParallel ?? target.MaxParallel;
        target.ContextFiles = layer.ContextFiles ?? target.ContextFiles;
        if (layer.Agents is { Count: > 0 })
        {
            target.Agents ??= new(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in layer.Agents)
                target.Agents[k] = v;
        }
    }

    private static string? ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>False for the scaffolded template, which only holds a heading and an HTML comment.</summary>
    private static bool HasContent(string markdown)
    {
        var withoutComments = System.Text.RegularExpressions.Regex.Replace(markdown, "<!--[\\s\\S]*?-->", "");
        return withoutComments.Split('\n').Any(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
    }

    private static class BuiltIn
    {
        private static readonly Assembly Assembly = typeof(ProfileLoader).Assembly;

        public static string Standards => Read("standards.md");
        public static string Context => Read("context.md");

        public static IEnumerable<(string Name, string Text)> Reviewers =>
            Assembly.GetManifestResourceNames()
                .Select(n => n.Replace('\\', '/'))
                .Where(n => n.StartsWith("defaults/reviewers/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(n => (Path.GetFileNameWithoutExtension(n), Read(n["defaults/".Length..])));

        private static string Read(string relative)
        {
            var name = Assembly.GetManifestResourceNames()
                .First(n => n.Replace('\\', '/') == "defaults/" + relative);
            using var stream = Assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}

/// <summary>Minimal frontmatter reader: <c>key: value</c> lines between <c>---</c> fences.</summary>
internal static class Frontmatter
{
    public static (Dictionary<string, string> Meta, string Body) Split(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = text.ReplaceLineEndings("\n");
        if (!normalized.StartsWith("---\n"))
            return (meta, normalized);

        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            return (meta, normalized);

        foreach (var line in normalized[4..end].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || line.TrimStart().StartsWith('#'))
                continue;
            meta[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"', '\'');
        }

        var bodyStart = normalized.IndexOf('\n', end + 1);
        return (meta, bodyStart < 0 ? "" : normalized[(bodyStart + 1)..]);
    }
}
