using System.Text;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;
using Prt.Core.Repos;

namespace Prt.Core.Panel;

/// <summary>Files written for one PR of a joint analysis.</summary>
public sealed record GroupMemberContext(
    PullRequest Pr,
    string? Head,
    IReadOnlyList<PrCommit>? NewCommits,
    CheckoutInfo? Checkout,
    string Dir,
    bool HasDiff,
    bool HasProjectContext,
    IReadOnlyList<string> ContextFiles)
{
    public string DiffPath => Path.Combine(Dir, "diff.patch");
    public string PrPath => Path.Combine(Dir, "pr.md");
    public string ContextPath => Path.Combine(Dir, "context.md");
}

/// <summary>Files written for a joint analysis of a PR group; reviewers read them instead of receiving them in the prompt.</summary>
public sealed record GroupRunContext(string Dir, PrGroup Group, IReadOnlyList<GroupMemberContext> Members)
{
    public string GroupPath => Path.Combine(Dir, "group.md");
    public string PreviousPath(string reviewer) => Path.Combine(Dir, $"previous-{reviewer}.json");

    public IReadOnlyList<string> PrKeys => Members.Select(m => m.Pr.Key).ToList();

    /// <summary>Code folders of the group, which the agents must be allowed to read.</summary>
    public IReadOnlyList<string> CodeDirs => Members.Select(m => m.Checkout?.Path).OfType<string>().Distinct().ToList();
}

/// <summary>Builds the prompts of a joint analysis: one reviewer sees every PR of the group at once.</summary>
public static class GroupPrompts
{
    public static string Initial(ReviewerDefinition reviewer, ReviewProfile profile, GroupRunContext context, bool hasPrevious)
    {
        var sb = new StringBuilder();
        sb.Append("Sos el revisor de **").Append(reviewer.Title).Append("** en un panel que revisa **en conjunto** un cambio coordinado en ")
            .Append(context.Members.Count).Append(" repositorios: el grupo «").Append(context.Group.Name).Append("». ")
            .Append("Los PRs implementan una misma feature y tienen que funcionar juntos. ")
            .Append("Otros revisores especializados analizan el mismo grupo con otros focos: concentrate en el tuyo.\n\n");

        AppendMaterial(sb, reviewer, context, hasPrevious);
        AppendJointRules(sb);
        if (hasPrevious)
            sb.Append("Ya revisaste este grupo antes: actualizá el `status` de cada hallazgo anterior (`still_open` o `resolved`) ")
                .Append("conservando su `id` y su `pr`, y agregá los nuevos con `status` `new`.\n\n");

        sb.Append("## Tu foco\n").Append(reviewer.Instructions.Trim()).Append("\n\n");
        sb.Append("## Estándar de revisión\n").Append(Export.MemoText.NestHeadings(profile.Standards.Trim(), 3)).Append("\n\n");
        sb.Append("## Formato de respuesta\n").Append(ReviewStandard.GroupResponseFormat(reviewer.Name, reviewer.IdPrefix, context.PrKeys));
        return sb.ToString();
    }

    public static string FollowUp(ReviewerDefinition reviewer, GroupRunContext context, bool hasPrevious)
    {
        var sb = new StringBuilder();
        sb.Append("Nueva pasada de revisión conjunta del grupo «").Append(context.Group.Name).Append("».\n\n");
        foreach (var m in context.Members)
        {
            sb.Append("- ").Append(m.Pr.Key);
            if (m.Head is not null)
                sb.Append(" (head ").Append(CommitHashes.Short(m.Head)).Append(')');
            if (m.NewCommits is { Count: > 0 } commits)
            {
                sb.Append(": commits nuevos desde tu última revisión:\n");
                foreach (var c in commits)
                    sb.Append("  - ").Append(c.ShortHash).Append(' ').Append(c.Subject).Append('\n');
            }
            else
            {
                sb.Append(": sin commits nuevos registrados.\n");
            }
        }
        sb.Append('\n');

        AppendMaterial(sb, reviewer, context, hasPrevious);
        AppendJointRules(sb);
        sb.Append(hasPrevious
            ? "Para cada hallazgo anterior indicá `status` `still_open` o `resolved` conservando su `id` y su `pr`; agregá los nuevos con `status` `new`.\n"
            : "Reportá todos los hallazgos vigentes.\n");
        sb.Append("## Tu foco (vigente)\n").Append(reviewer.Instructions.Trim()).Append("\n\n");
        sb.Append("Mantené el estándar de revisión de la primera pasada.\n\n");
        sb.Append("## Formato de respuesta\n").Append(ReviewStandard.GroupResponseFormat(reviewer.Name, reviewer.IdPrefix, context.PrKeys));
        return sb.ToString();
    }

    public static string FixFormat(ReviewerDefinition reviewer, GroupRunContext context, IReadOnlyList<string> errors) =>
        "Tu respuesta anterior no cumple el formato requerido (" + string.Join("; ", errors) + "). " +
        "Repetí el mismo contenido respetando exactamente este formato.\n\n" +
        ReviewStandard.GroupResponseFormat(reviewer.Name, reviewer.IdPrefix, context.PrKeys);

    /// <summary>Summary of the group handed to every reviewer.</summary>
    public static string GroupFile(PrGroup group, IReadOnlyList<GroupMemberContext> members)
    {
        var sb = new StringBuilder();
        sb.Append("# Grupo ").Append(group.Name).Append("\n\n");
        sb.Append(group.Description.Trim().Length > 0 ? group.Description.Trim() : "(sin descripción)").Append("\n\n");
        sb.Append("| PR | Título | Ramas | Head |\n|---|---|---|---|\n");
        foreach (var m in members)
        {
            sb.Append("| ").Append(m.Pr.Key).Append(" | ").Append(m.Pr.Title.Replace('|', '/')).Append(" | ")
                .Append(m.Pr.SourceBranch).Append(" → ").Append(m.Pr.DestinationBranch).Append(" | ")
                .Append(m.Head is null ? "" : CommitHashes.Short(m.Head)).Append(" |\n");
        }
        return sb.ToString();
    }

    private static void AppendMaterial(StringBuilder sb, ReviewerDefinition reviewer, GroupRunContext context, bool hasPrevious)
    {
        sb.Append("## Material\n");
        sb.Append("- Resumen del grupo: `").Append(context.GroupPath).Append("`\n");
        if (hasPrevious)
            sb.Append("- Tus hallazgos anteriores: `").Append(context.PreviousPath(reviewer.Name)).Append("`\n");
        sb.Append('\n');

        foreach (var m in context.Members)
        {
            sb.Append("### ").Append(m.Pr.Key).Append(" — ").Append(m.Pr.Title).Append('\n');
            sb.Append("- Ramas: ").Append(m.Pr.SourceBranch).Append(" → ").Append(m.Pr.DestinationBranch);
            if (m.Head is not null)
                sb.Append(" · head ").Append(CommitHashes.Short(m.Head));
            sb.Append('\n');
            sb.Append(m.HasDiff
                ? $"- Diff: `{m.DiffPath}`\n"
                : $"- No se pudo obtener el diff desde Bitbucket: en su código, usá `git diff origin/{m.Pr.DestinationBranch}...origin/{m.Pr.SourceBranch}`.\n");
            sb.Append("- Descripción, metadatos y commits: `").Append(m.PrPath).Append("`\n");
            if (m.HasProjectContext)
                sb.Append("- Contexto del proyecto (leelo antes de revisar): `").Append(m.ContextPath).Append("`\n");
            switch (m.Checkout)
            {
                case { Kind: CheckoutKind.Managed } managed:
                    sb.Append("- Código: `").Append(managed.Path).Append("`, en la rama de destino `").Append(managed.BaseBranch)
                        .Append("` (commit ").Append(CommitHashes.Short(managed.BaseCommit)).Append("), tal como está hoy. ")
                        .Append("Ahí podés usar `git diff origin/").Append(managed.BaseBranch).Append("...origin/").Append(m.Pr.SourceBranch).Append("`.\n");
                    break;
                case { } local:
                    sb.Append("- Código: `").Append(local.Path).Append("` (checkout local).\n");
                    break;
                default:
                    sb.Append("- Sin checkout del repositorio: trabajá con el diff.\n");
                    break;
            }
            foreach (var file in m.ContextFiles)
                sb.Append("- Documentación del proyecto: `").Append(file).Append("` (relativo a su código)\n");
            sb.Append('\n');
        }
        sb.Append("No modifiques archivos.\n\n");
    }

    private static void AppendJointRules(StringBuilder sb) =>
        sb.Append("## Revisión conjunta\n")
            .Append("- Revisá cada PR con tu foco y, además, cómo se relacionan: lo que un repo produce y otro consume ")
            .Append("(APIs, eventos, esquemas, configuración, paquetes) tiene que coincidir en ambos lados.\n")
            .Append("- Buscá cambios que faltan en algún repo y problemas del despliegue parcial (un PR en producción y el otro todavía no).\n")
            .Append("- Cada hallazgo va al PR donde hay que hacer el cambio (campo `pr`); si exige cambios coordinados en varios, usá `pr: null` ")
            .Append("y nombrá los repos en la descripción.\n\n");
}
