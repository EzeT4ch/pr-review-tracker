using System.Globalization;
using System.Text.RegularExpressions;
using Prt.Core.Domain;
using Prt.Core.Export;

namespace Prt.Core.Panel;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    /// <summary>"\ No newline at end of file" and similar markers.</summary>
    Note,
}

public enum DiffFileStatus
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Binary,
}

public sealed record DiffLine(DiffLineKind Kind, int? OldLine, int? NewLine, string Text);

public sealed record DiffHunk(string Header, IReadOnlyList<DiffLine> Lines);

public sealed record DiffFile(string OldPath, string NewPath, DiffFileStatus Status, IReadOnlyList<DiffHunk> Hunks)
{
    /// <summary>Path the PR leaves the file at (the old one for deletions).</summary>
    public string Path => Status == DiffFileStatus.Deleted ? OldPath : NewPath;

    public int Added => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));
    public int Removed => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Removed));
    public int LineCount => Hunks.Sum(h => h.Lines.Count + 1);
}

/// <summary>Parser for the unified (git) diff Bitbucket returns for a pull request.</summary>
public static partial class UnifiedDiff
{
    [GeneratedRegex(@"^@@ -(?<old>\d+)(?:,\d+)? \+(?<new>\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    [GeneratedRegex(@"^diff --git (?:""?a/(?<a>.+?)""?) (?:""?b/(?<b>.+?)""?)$")]
    private static partial Regex GitHeader();

    public static IReadOnlyList<DiffFile> Parse(string diff)
    {
        var files = new List<DiffFile>();
        string? oldPath = null, newPath = null;
        var status = DiffFileStatus.Modified;
        var hunks = new List<DiffHunk>();
        List<DiffLine>? lines = null;
        string? hunkHeader = null;
        int oldNo = 0, newNo = 0;

        void CloseHunk()
        {
            if (hunkHeader is not null && lines is not null)
                hunks.Add(new DiffHunk(hunkHeader, lines));
            hunkHeader = null;
            lines = null;
        }

        void CloseFile()
        {
            CloseHunk();
            if (oldPath is not null || newPath is not null)
                files.Add(new DiffFile(oldPath ?? newPath!, newPath ?? oldPath!, status, hunks));
            oldPath = newPath = null;
            status = DiffFileStatus.Modified;
            hunks = [];
        }

        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                CloseFile();
                var m = GitHeader().Match(line);
                if (m.Success)
                {
                    oldPath = m.Groups["a"].Value;
                    newPath = m.Groups["b"].Value;
                }
                continue;
            }

            if (hunkHeader is not null && line.Length > 0 && line[0] is ' ' or '+' or '-' or '\\')
            {
                switch (line[0])
                {
                    case '+':
                        lines!.Add(new DiffLine(DiffLineKind.Added, null, newNo++, line[1..]));
                        break;
                    case '-':
                        lines!.Add(new DiffLine(DiffLineKind.Removed, oldNo++, null, line[1..]));
                        break;
                    case '\\':
                        lines!.Add(new DiffLine(DiffLineKind.Note, null, null, line[1..].Trim()));
                        break;
                    default:
                        lines!.Add(new DiffLine(DiffLineKind.Context, oldNo++, newNo++, line[1..]));
                        break;
                }
                continue;
            }

            var hunk = HunkHeader().Match(line);
            if (hunk.Success)
            {
                CloseHunk();
                hunkHeader = line;
                lines = [];
                oldNo = int.Parse(hunk.Groups["old"].Value, CultureInfo.InvariantCulture);
                newNo = int.Parse(hunk.Groups["new"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            // File header lines (between "diff --git" and the first hunk).
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var path = line[4..].Trim('"');
                if (path == "/dev/null")
                    status = DiffFileStatus.Added;
                else
                    oldPath = StripPrefix(path, "a/");
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = line[4..].Trim('"');
                if (path == "/dev/null")
                    status = DiffFileStatus.Deleted;
                else
                    newPath = StripPrefix(path, "b/");
            }
            else if (line.StartsWith("new file mode", StringComparison.Ordinal))
                status = DiffFileStatus.Added;
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
                status = DiffFileStatus.Deleted;
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                oldPath = line["rename from ".Length..];
                status = DiffFileStatus.Renamed;
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                newPath = line["rename to ".Length..];
                status = DiffFileStatus.Renamed;
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                status = DiffFileStatus.Binary;
        }

        CloseFile();
        return files;
    }

    private static string StripPrefix(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;

    /// <summary>True when a finding's file refers to <paramref name="diffPath"/> (agents may give it relative to a subfolder).</summary>
    public static bool SamePath(string? findingPath, string diffPath)
    {
        if (string.IsNullOrWhiteSpace(findingPath))
            return false;
        var a = findingPath.Replace('\\', '/').TrimStart('.', '/');
        var b = diffPath.Replace('\\', '/');
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
               || b.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase)
               || a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where <see cref="PanelService"/> keeps the diff a run analysed.</summary>
    public static string RunDiffPath(string runsRoot, PrRef pr, long runId) =>
        System.IO.Path.Combine(runsRoot, ObsidianExporter.SafeName(pr.Workspace), ObsidianExporter.SafeName(pr.Repo),
            pr.Number.ToString(CultureInfo.InvariantCulture), runId.ToString(CultureInfo.InvariantCulture), "diff.patch");
}
