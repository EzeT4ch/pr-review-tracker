using System.Globalization;
using System.Text;
using Prt.Core.Domain;
using Prt.Core.Panel;

namespace Prt.Core.Export;

public sealed partial class ObsidianExporter
{
    /// <summary>Folder of the PR group notes.</summary>
    public string GroupsDirectory => Path.Combine(RootDir, "_grupos");

    // The id keeps the note when the group is renamed.
    private static string GroupFileName(PrGroup group)
    {
        var name = SafeName(group.Name);
        if (name.Length > 80)
            name = name[..80].TrimEnd(' ', '.');
        return $"G{group.Id} - {name}.md";
    }

    private string GroupLink(PrGroup group) => $"{_folder}/_grupos/{Path.GetFileNameWithoutExtension(GroupFileName(group))}";

    /// <summary>Writes (or regenerates) the note of a PR group, with its joint analyses, and returns its path.</summary>
    public string ExportGroupNote(PrGroup group, IReadOnlyList<PullRequest> members, IReadOnlyList<GroupRunHistory> runs)
    {
        var dir = GroupsDirectory;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, GroupFileName(group));
        var existing = Directory.EnumerateFiles(dir, $"G{group.Id} - *.md").FirstOrDefault();
        if (existing is not null && !string.Equals(existing, path, StringComparison.Ordinal))
            File.Move(existing, path, overwrite: true);

        var previous = File.Exists(path) ? File.ReadAllText(path) : null;
        var (before, after) = UserContent(previous);
        var latest = runs.Count > 0 ? RunView.From(runs[^1]) : null;

        var sb = new StringBuilder("---\n");
        Yaml(sb, "group", group.Name);
        sb.Append("group_id: ").Append(group.Id).Append('\n');
        Yaml(sb, "verdict", group.Verdict ?? "");
        sb.Append("prs:");
        AppendList(sb, members.Select(m => m.Key));
        if (latest is not null)
        {
            Yaml(sb, "last_analysis", Local(latest.Run.StartedAt).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
            sb.Append("blockers: ").Append(latest.Count(Severity.Blocker)).Append('\n');
            sb.Append("majors: ").Append(latest.Count(Severity.Major)).Append('\n');
            sb.Append("open_findings: ").Append(latest.OpenFindings.Count()).Append('\n');
        }
        sb.Append("tags:\n  - \"pr-review-group\"\n---\n");
        sb.Append(before).Append(BeginMarker).Append('\n');

        sb.Append("# Grupo · ").Append(group.Name).Append("\n\n");
        if (group.Description.Trim().Length > 0)
            sb.Append(MarkdownText.Prose(group.Description.Trim())).Append("\n\n");
        sb.Append("Análisis conjunto: `prt group analyze ").Append(group.Id).Append("`\n\n");

        sb.Append("## PRs\n\n| PR | Título | Ramas | Estado | Veredicto |\n|---|---|---|---|---|\n");
        foreach (var pr in members)
        {
            sb.Append("| [[").Append(NoteLink(pr)).Append("\\|").Append(pr.Key).Append("]] | ").Append(MarkdownText.Cell(pr.Title))
                .Append(" | ").Append(MarkdownText.CodeCell($"{pr.SourceBranch} → {pr.DestinationBranch}"))
                .Append(" | ").Append(MarkdownText.Cell(pr.State)).Append(" | ").Append(MarkdownText.Cell(pr.Verdict ?? "")).Append(" |\n");
        }
        sb.Append('\n');

        sb.Append("## Análisis conjunto\n\n");
        if (latest is null)
            sb.Append("_Sin análisis todavía._\n\n");
        else
        {
            sb.Append(ReportRenderer.Render(latest, Display, level: 3, links: true));
            if (runs.Count > 1)
            {
                sb.Append("Análisis anteriores: ").Append(string.Join(" · ", runs.Take(runs.Count - 1).Reverse()
                    .Select(r => $"{Display(r.Run.StartedAt)} {r.Run.Verdict}"))).Append("\n\n");
            }
        }

        sb.Append(EndMarker).Append(previous is null ? "\n" : after);
        if (sb[^1] != '\n')
            sb.Append('\n');
        WriteAtomic(path, sb.ToString());
        return path;
    }
}
