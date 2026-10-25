using System.Text.RegularExpressions;

namespace Prt.Core.Config;

/// <summary>Finds local git checkouts of Bitbucket repositories, to suggest them as agent working directories.</summary>
public static partial class GitCheckout
{
    /// <summary>The root of the git checkout containing <paramref name="dir"/>, or null.</summary>
    public static string? FindRoot(string dir)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(dir)); current is not null; current = current.Parent)
        {
            var git = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return current.FullName;
        }
        return null;
    }

    /// <summary>True when one of the checkout's remotes points to bitbucket.org/&lt;workspace&gt;/&lt;repo&gt;.</summary>
    public static bool HasBitbucketRemote(string root, string repoKey)
    {
        if (ConfigPath(root) is not { } config || !File.Exists(config))
            return false;

        var parts = repoKey.Split('/');
        if (parts.Length != 2)
            return false;
        var pattern = new Regex(
            $@"bitbucket\.org[:/]{Regex.Escape(parts[0])}/{Regex.Escape(parts[1])}(\.git)?/?\s*$",
            RegexOptions.IgnoreCase);
        return File.ReadLines(config)
            .Select(l => UrlLine().Match(l))
            .Any(m => m.Success && pattern.IsMatch(m.Groups["url"].Value));
    }

    /// <summary>The checkout of <paramref name="repoKey"/> that contains <paramref name="dir"/>, if any.</summary>
    public static string? FindFor(string dir, string repoKey) =>
        FindRoot(dir) is { } root && HasBitbucketRemote(root, repoKey) ? root : null;

    [GeneratedRegex(@"^\s*url\s*=\s*(?<url>.+?)\s*$")]
    private static partial Regex UrlLine();

    private static string? ConfigPath(string root)
    {
        var git = Path.Combine(root, ".git");
        if (Directory.Exists(git))
            return Path.Combine(git, "config");

        // Worktrees and submodules: ".git" is a file with "gitdir: <path>".
        var line = File.ReadLines(git).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
        if (line is null)
            return null;
        var gitDir = Path.GetFullPath(Path.Combine(root, line["gitdir:".Length..].Trim()));
        var commonDir = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonDir))
            gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonDir).Trim()));
        return Path.Combine(gitDir, "config");
    }
}
