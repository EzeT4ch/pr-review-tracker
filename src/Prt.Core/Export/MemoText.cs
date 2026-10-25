using System.Text;
using System.Text.RegularExpressions;

namespace Prt.Core.Export;

/// <summary>Helpers to read structure out of agent memos and to embed Markdown inside notes.</summary>
public static partial class MemoText
{
    [GeneratedRegex(@"^[\s>*_`-]*veredicto[*_`\s]*[:\-–][*_`\s]*(?<v>.+?)[*_`\s]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VerdictPattern();

    [GeneratedRegex(@"^(?<hashes>#{1,6})(?=\s)", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    /// <summary>Last "Veredicto: X" line in the memo.</summary>
    public static string? Verdict(string memo)
    {
        var match = VerdictPattern().Matches(memo).LastOrDefault();
        var verdict = match?.Groups["v"].Value.Trim().Trim('`', '*', '_').Trim();
        return string.IsNullOrEmpty(verdict) ? null : verdict;
    }

    /// <summary>Body of the section with the given heading (e.g. "Pendientes"), without the verdict line.</summary>
    public static string? Section(string memo, string heading)
    {
        var lines = memo.ReplaceLineEndings("\n").Split('\n');
        var start = -1;
        var level = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^(#{1,6})\s+(.*?)\s*#*\s*$");
            if (!m.Success)
                continue;
            if (start >= 0 && m.Groups[1].Length <= level)
                return Clean(lines[start..i]);
            if (start < 0 && string.Equals(m.Groups[2].Value.Trim('*', ' '), heading, StringComparison.OrdinalIgnoreCase))
            {
                start = i + 1;
                level = m.Groups[1].Length;
            }
        }
        return start >= 0 ? Clean(lines[start..]) : null;

        static string? Clean(string[] body)
        {
            var text = string.Join('\n', body.Where(l => !VerdictPattern().IsMatch(l))).Trim();
            return text.Length == 0 ? null : text;
        }
    }

    /// <summary>Shifts headings so the shallowest becomes <paramref name="minLevel"/> (outside code fences).</summary>
    public static string NestHeadings(string markdown, int minLevel)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var inFence = false;
        var shallowest = int.MaxValue;
        foreach (var line in lines)
        {
            if (IsFence(line))
                inFence = !inFence;
            else if (!inFence && HeadingPattern().Match(line) is { Success: true } m)
                shallowest = Math.Min(shallowest, m.Groups["hashes"].Length);
        }
        if (shallowest == int.MaxValue || shallowest >= minLevel)
            return string.Join('\n', lines);

        var shift = minLevel - shallowest;
        var sb = new StringBuilder();
        inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (IsFence(line))
                inFence = !inFence;
            else if (!inFence && HeadingPattern().Match(line) is { Success: true } m)
                line = new string('#', Math.Min(6, m.Groups["hashes"].Length + shift)) + line[m.Groups["hashes"].Length..];
            sb.Append(line);
            if (i < lines.Length - 1)
                sb.Append('\n');
        }
        return sb.ToString();
    }

    private static bool IsFence(string line) => line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~");
}
