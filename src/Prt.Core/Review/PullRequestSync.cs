using Prt.Core.Bitbucket;
using Prt.Core.Domain;
using Prt.Core.Export;
using Prt.Core.Storage;

namespace Prt.Core.Review;

/// <summary>Keeps the stored PR metadata in line with Bitbucket.</summary>
public sealed class PullRequestSync(SqliteStore store, IPullRequestSource bitbucket, IStatusUi ui)
{
    /// <summary>Fetches PR metadata and commits and stores them; falls back to the stored copy when offline.</summary>
    public async Task<(PullRequest Pr, IReadOnlyList<PrCommit> Commits)> RefreshAsync(PrRef prRef, CancellationToken ct = default)
    {
        PullRequestInfo info;
        try
        {
            info = await bitbucket.GetPullRequestAsync(prRef, ct);
        }
        catch (Exception ex) when (ex is BitbucketException or HttpRequestException && store.FindPullRequest(prRef) is not null)
        {
            ui.Warn($"No se pudo consultar Bitbucket ({ex.Message}); uso los datos guardados.");
            return (store.FindPullRequest(prRef)!, []);
        }

        IReadOnlyList<PrCommit> commits;
        try
        {
            commits = await bitbucket.GetCommitsAsync(prRef, ct);
        }
        catch (Exception ex) when (ex is BitbucketException or HttpRequestException)
        {
            ui.Warn($"No se pudieron leer los commits del PR: {ex.Message}");
            commits = info.HeadCommit is null ? [] : [new PrCommit(info.HeadCommit, "", null)];
        }

        var pr = store.UpsertPullRequest(new PullRequest
        {
            Workspace = prRef.Workspace,
            Repo = prRef.Repo,
            Number = prRef.Number,
            Title = info.Title,
            Author = info.Author,
            SourceBranch = info.SourceBranch,
            DestinationBranch = info.DestinationBranch,
            State = info.State,
            Url = info.Url,
            Description = info.Description,
        });
        return (pr, commits);
    }
}

public static class VaultExport
{
    /// <summary>Regenerates the note of a PR (and its panel reports) plus the index; returns the note path.</summary>
    public static string Export(this ObsidianExporter exporter, SqliteStore store, long prId)
    {
        var path = exporter.ExportPr(store.GetHistory(prId));
        exporter.ExportIndex(store);
        return path;
    }

    public static string ExportIndex(this ObsidianExporter exporter, SqliteStore store) =>
        exporter.ExportIndex(store.ListPullRequests().Select(p => store.GetHistory(p.Id)).ToList());
}
