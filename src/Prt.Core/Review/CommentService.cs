using System.Globalization;
using System.Text;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;
using Prt.Core.Export;
using Prt.Core.Panel;
using Prt.Core.Storage;

namespace Prt.Core.Review;

/// <summary>Comments on the PR in Bitbucket: publishing them from prt, and handing the existing ones to the reviewers.</summary>
public sealed class CommentService(SqliteStore store, IPullRequestSource bitbucket, ObsidianExporter? exporter)
{
    /// <summary>Publishes the comment in Bitbucket and keeps a copy for the timeline and the note.</summary>
    public async Task<PrLineComment> PostAsync(PrRef prRef, NewComment comment, string? findingId = null, CancellationToken ct = default)
    {
        var pr = store.FindPullRequest(prRef) ?? throw new ArgumentException($"{prRef.Key} no está registrado.");
        var published = await bitbucket.AddCommentAsync(prRef, comment, ct);
        var local = store.AddLineComment(new PrLineComment
        {
            PrId = pr.Id,
            RemoteId = published.Id,
            ParentRemoteId = comment.ParentId,
            Path = comment.Path,
            Line = comment.Line,
            OldSide = comment.OldSide,
            Body = comment.Body,
            FindingId = findingId,
            Url = published.Url,
        });
        exporter?.Export(store, pr.Id);
        return local;
    }

    /// <summary>Text of a comment that publishes a panel finding; meant to be edited before sending.</summary>
    public static string FindingComment(string reviewer, Finding f)
    {
        var sb = new StringBuilder();
        sb.Append("**[").Append(f.Id).Append("] ").Append(f.Title).Append("** · ").Append(ReviewStandard.Label(f.Severity).ToLowerInvariant())
            .Append(" · ").Append(reviewer).Append("\n\n");
        if (f.Description.Trim().Length > 0)
            sb.Append(f.Description.Trim()).Append("\n\n");
        if (f.Recommendation.Trim().Length > 0)
            sb.Append("**Recomendación:** ").Append(f.Recommendation.Trim()).Append("\n\n");
        sb.Append("_Generado con prt_");
        return sb.ToString();
    }

    /// <summary>The PR's comments as Markdown for the reviewers: by file and line, each thread with its replies.</summary>
    public static string CommentsFile(PullRequest pr, IReadOnlyList<PrComment> comments)
    {
        var byId = comments.ToDictionary(c => c.Id);
        // A reply whose parent is not in the list (deleted) starts its own thread.
        var roots = comments.Where(c => c.ParentId is not { } p || !byId.ContainsKey(p)).ToList();
        var replies = comments.Where(c => c.ParentId is { } p && byId.ContainsKey(p)).ToLookup(c => c.ParentId!.Value);

        var sb = new StringBuilder();
        sb.Append("# Comentarios del PR ").Append(pr.Key).Append("\n\n");
        sb.Append("Comentarios que ya dejaron personas en Bitbucket (").Append(comments.Count).Append("). ")
            .Append("Las líneas son del diff: \"nueva\" en el archivo del PR, \"anterior\" en el de la rama de destino.\n");

        foreach (var file in roots.Where(c => c.IsInline).GroupBy(c => c.Path!).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append("\n## `").Append(file.Key).Append("`\n");
            foreach (var root in file.OrderBy(c => c.To ?? c.From ?? 0).ThenBy(c => c.CreatedAt))
            {
                sb.Append("\n### ").Append(root.To is { } to ? $"Línea {to} (nueva)" : root.From is { } from ? $"Línea {from} (anterior)" : "Archivo").Append('\n');
                AppendThread(sb, root, replies, 0);
            }
        }

        var general = roots.Where(c => !c.IsInline).ToList();
        if (general.Count > 0)
        {
            sb.Append("\n## Comentarios generales\n");
            foreach (var root in general)
            {
                sb.Append('\n');
                AppendThread(sb, root, replies, 0);
            }
        }
        return sb.ToString();
    }

    private static void AppendThread(StringBuilder sb, PrComment comment, ILookup<long, PrComment> replies, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.Append(indent).Append("- **").Append(comment.Author).Append("** (")
            .Append(comment.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("): ");
        var lines = comment.Body.Trim().ReplaceLineEndings("\n").Split('\n');
        sb.Append(lines[0]).Append('\n');
        foreach (var line in lines.Skip(1))
            sb.Append(indent).Append("  ").Append(line).Append('\n');
        foreach (var reply in replies[comment.Id].OrderBy(r => r.CreatedAt))
            AppendThread(sb, reply, replies, depth + 1);
    }
}
