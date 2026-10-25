using System.Text;
using System.Text.RegularExpressions;

namespace Prt.Core.Export;

/// <summary>
/// Escaping for text written by agents or Bitbucket users before it lands in an Obsidian note. Code spans and
/// fenced blocks are left as they are; Markdown structure (headings, lists, emphasis, tables) is kept.
/// </summary>
public static partial class MarkdownText
{
    // `code`, ``code with ` inside``: the closing run has the same length as the opening one.
    [GeneratedRegex(@"(?<!`)(`+)(?!`).*?(?<!`)\1(?!`)")]
    private static partial Regex CodeSpan();

    // What Obsidian would otherwise interpret inside plain text:
    // <Type> as an HTML tag (it disappears), %% comments, $ math, ==highlight==, [[wikilinks]] and #tags.
    // What the author already escaped is left alone.
    [GeneratedRegex(@"(?<!\\)(?:<(?=[A-Za-z/!])(?!https?://)|%%|\$|==|\[\[|\]\]|(?<=^|\s)#(?=[\p{L}_]))")]
    private static partial Regex Special();

    /// <summary>Escapes multi-line Markdown (descriptions, memos, questions) outside code.</summary>
    public static string Prose(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsFence(lines[i]))
                inFence = !inFence;
            else if (!inFence)
                lines[i] = EscapeOutsideCode(lines[i]);
        }
        return string.Join('\n', lines);
    }

    /// <summary>Text for a table cell: one line, escaped, with pipes escaped (also inside code, as GFM tables require).</summary>
    public static string Cell(string text) => Prose(text.ReplaceLineEndings(" ")).Replace("|", "\\|");

    /// <summary>Inline code that survives backticks inside the value.</summary>
    public static string Code(string text) => text.Contains('`') ? $"`` {text} ``" : $"`{text}`";

    /// <summary>Inline code for a table cell.</summary>
    public static string CodeCell(string text) => Code(text.ReplaceLineEndings(" ")).Replace("|", "\\|");

    /// <summary>One line of at most <paramref name="max"/> characters, without leaving a code span open.</summary>
    public static string Truncate(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ");
        if (line.Length <= max)
            return line;
        var cut = line[..(max - 1)].TrimEnd();
        if (cut.Count(c => c == '`') % 2 == 1)
            cut += "`";
        return cut + "…";
    }

    private static string EscapeOutsideCode(string line)
    {
        var sb = new StringBuilder(line.Length);
        var last = 0;
        foreach (Match code in CodeSpan().Matches(line))
        {
            sb.Append(Escape(line[last..code.Index])).Append(code.Value);
            last = code.Index + code.Length;
        }
        return sb.Append(Escape(line[last..])).ToString();
    }

    private static string Escape(string text) =>
        Special().Replace(text, m => string.Concat(m.Value.Select(c => "\\" + c)));

    private static bool IsFence(string line) => line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~");
}
