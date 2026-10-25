using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Prt.Core.Config;

namespace Prt.Core.Panel;

/// <summary>
/// Writes reviewer definitions into a layer folder (global, the repo's .prt/ or the user's project folder).
/// Used by the web panel; the files are the same ones a person would edit by hand.
/// </summary>
public static partial class ReviewerFiles
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Z0-9]{2,8}$")]
    private static partial Regex PrefixPattern();

    public static string PathOf(string layerDir, string name) => Path.Combine(layerDir, "reviewers", name + ".md");

    public static bool Exists(string layerDir, string name) => File.Exists(PathOf(layerDir, name));

    /// <summary>Validates and writes <c>reviewers/&lt;name&gt;.md</c> in the layer.</summary>
    public static string Save(string layerDir, ReviewerDefinition reviewer)
    {
        Validate(reviewer);
        var sb = new StringBuilder("---\n");
        sb.Append("name: ").Append(reviewer.Name).Append('\n');
        sb.Append("title: ").Append(OneLine(reviewer.Title)).Append('\n');
        sb.Append("id_prefix: ").Append(reviewer.IdPrefix).Append('\n');
        if (reviewer.Agent is { Length: > 0 } agent)
            sb.Append("agent: ").Append(agent).Append('\n');
        sb.Append("enabled: ").Append(reviewer.Enabled ? "true" : "false").Append('\n');
        sb.Append("order: ").Append(reviewer.Order.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("---\n").Append(reviewer.Instructions.Trim().ReplaceLineEndings("\n")).Append('\n');

        var path = PathOf(layerDir, reviewer.Name);
        WriteAtomic(path, sb.ToString());
        return path;
    }

    /// <summary>Removes the layer's definition of a reviewer; the next layer down (or the built-in one) applies again.</summary>
    public static bool Delete(string layerDir, string name)
    {
        var path = PathOf(layerDir, name);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Reads, changes and writes the layer's review.json (created when missing).</summary>
    public static void UpdateSettings(string layerDir, Action<ProjectReviewSettings> change)
    {
        var path = Path.Combine(layerDir, "review.json");
        var settings = File.Exists(path)
            ? ProfileLoader.ParseSettings(File.ReadAllText(path), path)
            : new ProjectReviewSettings();
        change(settings);
        WriteAtomic(path, JsonSerializer.Serialize(settings, ProfileLoader.JsonOptions) + "\n");
    }

    /// <summary>
    /// Saves a reviewer edited as a whole and keeps review.json consistent with it: when the effective profile
    /// has a <c>reviewers</c> list or an <c>agents</c> entry for it, those would override the file, so the
    /// same layer gets the updated list or agent.
    /// </summary>
    public static string SaveEdited(string layerDir, ReviewerDefinition reviewer, ReviewProfile current)
    {
        var path = Save(layerDir, reviewer);

        var list = current.ExplicitReviewers;
        var listChanges = list is not null &&
                          list.Contains(reviewer.Name, StringComparer.OrdinalIgnoreCase) != reviewer.Enabled;
        var agentOverridden = current.AgentOverrides.Keys.Contains(reviewer.Name, StringComparer.OrdinalIgnoreCase);
        if (!listChanges && !agentOverridden)
            return path;

        UpdateSettings(layerDir, settings =>
        {
            if (listChanges)
            {
                var names = list!.Where(n => !string.Equals(n, reviewer.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (reviewer.Enabled)
                    names.Add(reviewer.Name);
                settings.Reviewers = names;
            }
            if (agentOverridden)
            {
                settings.Agents = new Dictionary<string, string>(settings.Agents ?? [], StringComparer.OrdinalIgnoreCase);
                if (reviewer.Agent is { Length: > 0 } agent)
                    settings.Agents[reviewer.Name] = agent;
                else
                    settings.Agents.Remove(reviewer.Name);
            }
        });
        return path;
    }

    public static void Validate(ReviewerDefinition reviewer)
    {
        if (!NamePattern().IsMatch(reviewer.Name))
            throw new ArgumentException("El nombre del revisor va en minúsculas, con letras, números o guiones (ej. pci o accesibilidad).");
        if (reviewer.Title.Trim().Length == 0)
            throw new ArgumentException("El revisor necesita un título.");
        if (!PrefixPattern().IsMatch(reviewer.IdPrefix))
            throw new ArgumentException("El prefijo de los hallazgos va en mayúsculas, de 2 a 8 letras o números (ej. SEC).");
        if (reviewer.Instructions.Trim().Length == 0)
            throw new ArgumentException("Las instrucciones del revisor no pueden quedar vacías: son su foco de revisión.");
        if (reviewer.Agent is { Length: > 0 } agent && !AgentNames.All.Contains(agent, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Agente desconocido '{agent}'. Opciones: {string.Join(", ", AgentNames.All)}.");
    }

    private static string OneLine(string value) => value.ReplaceLineEndings(" ").Trim();

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".prt-tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
