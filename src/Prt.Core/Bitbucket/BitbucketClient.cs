using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Prt.Core.Config;
using Prt.Core.Domain;

namespace Prt.Core.Bitbucket;

public sealed record PrCommit(string Hash, string Message, DateTimeOffset? Date)
{
    public string ShortHash => Hash.Length > 12 ? Hash[..12] : Hash;
    public string Subject => Message.Split('\n', 2)[0].Trim();
}

public sealed record PullRequestInfo(
    PrRef Ref,
    string Title,
    string Description,
    string State,
    string Author,
    string SourceBranch,
    string DestinationBranch,
    string? HeadCommit,
    string Url);

public interface IPullRequestSource
{
    Task<PullRequestInfo> GetPullRequestAsync(PrRef prRef, CancellationToken ct = default);

    /// <summary>PR commits, newest first.</summary>
    Task<IReadOnlyList<PrCommit>> GetCommitsAsync(PrRef prRef, CancellationToken ct = default);

    /// <summary>Unified diff of the PR (source against the merge base with the destination).</summary>
    Task<string> GetDiffAsync(PrRef prRef, CancellationToken ct = default);
}

public sealed class BitbucketException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Minimal Bitbucket Cloud REST API 2.0 client.</summary>
public sealed class BitbucketClient : IPullRequestSource
{
    private const int MaxCommitPages = 10;
    private const int MaxRedirects = 5;
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public BitbucketClient(HttpClient http, BitbucketSettings settings)
    {
        _http = http;
        _baseUrl = settings.BaseUrl.TrimEnd('/');
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("prt", "1.0"));
        if (settings.EffectiveUsername is { Length: > 0 } user && settings.EffectiveToken is { Length: > 0 } token)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }
    }

    public async Task<PullRequestInfo> GetPullRequestAsync(PrRef prRef, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"{RepoUrl(prRef)}/pullrequests/{prRef.Number}", ct);
        var root = doc.RootElement;
        return new PullRequestInfo(
            prRef,
            Title: Str(root, "title"),
            Description: Str(root, "description"),
            State: Str(root, "state"),
            Author: Str(root, "author", "display_name") is { Length: > 0 } name ? name : Str(root, "author", "nickname"),
            SourceBranch: Str(root, "source", "branch", "name"),
            DestinationBranch: Str(root, "destination", "branch", "name"),
            HeadCommit: Str(root, "source", "commit", "hash") is { Length: > 0 } hash ? hash : null,
            Url: Str(root, "links", "html", "href") is { Length: > 0 } url ? url : prRef.WebUrl);
    }

    public async Task<IReadOnlyList<PrCommit>> GetCommitsAsync(PrRef prRef, CancellationToken ct = default)
    {
        var commits = new List<PrCommit>();
        string? next = $"{RepoUrl(prRef)}/pullrequests/{prRef.Number}/commits?pagelen=50";
        for (var page = 0; next is not null && page < MaxCommitPages; page++)
        {
            using var doc = await GetJsonAsync(next, ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("values", out var values))
            {
                foreach (var value in values.EnumerateArray())
                {
                    DateTimeOffset? date = value.TryGetProperty("date", out var d) && d.TryGetDateTimeOffset(out var parsed)
                        ? parsed
                        : null;
                    commits.Add(new PrCommit(Str(value, "hash"), Str(value, "message"), date));
                }
            }
            next = root.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        return commits;
    }

    public async Task<string> GetDiffAsync(PrRef prRef, CancellationToken ct = default)
    {
        using var response = await GetAsync($"{RepoUrl(prRef)}/pullrequests/{prRef.Number}/diff", ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private string RepoUrl(PrRef prRef) =>
        $"{_baseUrl}/repositories/{Uri.EscapeDataString(prRef.Workspace)}/{Uri.EscapeDataString(prRef.Repo)}";

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await GetAsync(url, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>
    /// GET following redirects by hand: HttpClient drops the Authorization header when it follows them,
    /// and Bitbucket redirects diff requests. Use an HttpClient whose handler does not auto-redirect.
    /// </summary>
    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        var current = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            var response = await _http.GetAsync(current, ct);
            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location && hop < MaxRedirects)
            {
                response.Dispose();
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (response.IsSuccessStatusCode)
                return response;

            using (response)
                throw Failure(response, current.ToString());
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static BitbucketException Failure(HttpResponseMessage response, string url)
    {
        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " Revisá usuario/API token (prt init o PRT_BB_USER/PRT_BB_TOKEN).",
            HttpStatusCode.Forbidden => " El token no tiene permiso de lectura de pull requests sobre ese repo.",
            HttpStatusCode.NotFound => " El PR no existe o el repo es privado y faltan credenciales.",
            _ => "",
        };
        return new BitbucketException(
            $"Bitbucket respondió {(int)response.StatusCode} {response.ReasonPhrase} para {url}.{hint}",
            response.StatusCode);
    }

    private static string Str(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                return "";
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";
    }
}

public static class CommitHashes
{
    /// <summary>Bitbucket mixes short (12 char) and full hashes; compare by prefix.</summary>
    public static bool Same(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase));

    /// <summary>Commits (newest first) added after <paramref name="sinceHash"/>; null if it is not in the list.</summary>
    public static IReadOnlyList<PrCommit>? Since(IReadOnlyList<PrCommit> newestFirst, string sinceHash)
    {
        var index = -1;
        for (var i = 0; i < newestFirst.Count; i++)
        {
            if (Same(newestFirst[i].Hash, sinceHash))
            {
                index = i;
                break;
            }
        }
        return index < 0 ? null : newestFirst.Take(index).ToList();
    }

    public static string Short(string? hash) => hash is null ? "" : hash.Length > 12 ? hash[..12] : hash;
}
