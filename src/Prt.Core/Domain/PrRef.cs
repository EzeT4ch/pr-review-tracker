using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Prt.Core.Domain;

/// <summary>Identifies a Bitbucket Cloud pull request: workspace/repo#number.</summary>
public sealed partial record PrRef(string Workspace, string Repo, int Number)
{
    public string RepoKey => $"{Workspace}/{Repo}";
    public string Key => $"{Workspace}/{Repo}#{Number}";
    public string WebUrl => $"https://bitbucket.org/{Workspace}/{Repo}/pull-requests/{Number}";

    public override string ToString() => Key;

    // https://bitbucket.org/ws/repo/pull-requests/12[/diff|/overview|?...]
    [GeneratedRegex(@"^https?://(?:www\.)?bitbucket\.org/(?<ws>[^/\s]+)/(?<repo>[^/\s]+)/pull-requests/(?<n>\d+)(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    // ws/repo#12 or ws/repo/12
    [GeneratedRegex(@"^(?<ws>[^/\s#]+)/(?<repo>[^/\s#]+)(?:#|/)(?<n>\d+)$")]
    private static partial Regex ShortPattern();

    public static bool TryParse(string? text, [NotNullWhen(true)] out PrRef? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        var match = UrlPattern().Match(trimmed);
        if (!match.Success)
            match = ShortPattern().Match(trimmed);
        if (!match.Success || !int.TryParse(match.Groups["n"].Value, out var number) || number <= 0)
            return false;

        result = new PrRef(match.Groups["ws"].Value, match.Groups["repo"].Value, number);
        return true;
    }

    public static PrRef Parse(string text) =>
        TryParse(text, out var result)
            ? result
            : throw new FormatException(
                $"'{text}' no es un PR válido. Usá la URL de Bitbucket o el formato workspace/repo#numero.");
}
