using System.Text;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;
using Prt.Core.Repos;

namespace Prt.Core.Panel;

/// <summary>Files written for a run; reviewers read them instead of receiving them in the prompt.</summary>
public sealed record RunContext(
    string Dir,
    bool HasDiff,
    bool HasProjectContext,
    CheckoutInfo? Checkout,
    IReadOnlyList<string> ContextFiles,
    bool HasComments = false)
{
    public bool HasCheckout => Checkout is not null;

    public string CommentsPath => Path.Combine(Dir, "comments.md");

    public string DiffPath => Path.Combine(Dir, "diff.patch");
    public string PrPath => Path.Combine(Dir, "pr.md");
    public string ContextPath => Path.Combine(Dir, "context.md");
    public string PreviousPath(string reviewer) => Path.Combine(Dir, $"previous-{reviewer}.json");
}

/// <summary>Builds the prompts of the panel. The same text goes to every agent provider.</summary>
public static class PanelPrompts
{
    public static string Initial(ReviewerDefinition reviewer, ReviewProfile profile, PullRequest pr, string? head,
        RunContext context, bool hasPrevious)
    {
        var sb = new StringBuilder();
        sb.Append("Sos el revisor de **").Append(reviewer.Title).Append("** en un panel de revisión de código. ")
            .Append("Otros revisores especializados analizan el mismo PR con otros focos: concentrate en el tuyo.\n\n");

        sb.Append("## Pull Request\n");
        sb.Append("- PR: ").Append(pr.Key).Append(" — ").Append(pr.Title).Append('\n');
        sb.Append("- URL: ").Append(pr.Url).Append('\n');
        sb.Append("- Autor: ").Append(pr.Author).Append('\n');
        sb.Append("- Ramas: ").Append(pr.SourceBranch).Append(" → ").Append(pr.DestinationBranch).Append('\n');
        if (head is not null)
            sb.Append("- Head: ").Append(CommitHashes.Short(head)).Append('\n');
        sb.Append('\n');

        AppendMaterial(sb, reviewer, pr, context, hasPrevious);
        if (hasPrevious)
            sb.Append("Ya revisaste este PR antes: actualizá el `status` de cada hallazgo anterior (`still_open` o `resolved`) ")
                .Append("conservando su `id`, y agregá los nuevos con `status` `new`.\n\n");

        sb.Append("## Tu foco\n").Append(reviewer.Instructions.Trim()).Append("\n\n");
        sb.Append("## Estándar de revisión\n").Append(DemoteHeadings(profile.Standards)).Append("\n\n");
        sb.Append("## Formato de respuesta\n").Append(ReviewStandard.ResponseFormat(reviewer.Name, reviewer.IdPrefix));
        return sb.ToString();
    }

    public static string FollowUp(ReviewerDefinition reviewer, PullRequest pr, string? head,
        IReadOnlyList<PrCommit>? newCommits, RunContext context, bool hasPrevious)
    {
        var sb = new StringBuilder();
        sb.Append("Nueva pasada de revisión del PR ").Append(pr.Key);
        if (head is not null)
            sb.Append(" (head ").Append(CommitHashes.Short(head)).Append(')');
        sb.Append(".\n\n");

        if (newCommits is { Count: > 0 })
        {
            sb.Append("Commits nuevos desde tu última revisión:\n");
            foreach (var c in newCommits)
                sb.Append("- ").Append(c.ShortHash).Append(' ').Append(c.Subject).Append('\n');
        }
        else
        {
            sb.Append("No hay commits nuevos registrados; repetí la revisión con el material actual.\n");
        }
        sb.Append('\n');

        AppendMaterial(sb, reviewer, pr, context, hasPrevious);
        sb.Append(hasPrevious
            ? "Para cada hallazgo anterior indicá `status` `still_open` o `resolved` conservando su `id`; agregá los nuevos con `status` `new`.\n"
            : "Reportá todos los hallazgos vigentes.\n");
        // Always restated: the reviewer may have been edited since the session started.
        sb.Append("## Tu foco (vigente)\n").Append(reviewer.Instructions.Trim()).Append("\n\n");
        sb.Append("Mantené el estándar de revisión de la primera pasada.\n\n");
        sb.Append("## Formato de respuesta\n").Append(ReviewStandard.ResponseFormat(reviewer.Name, reviewer.IdPrefix));
        return sb.ToString();
    }

    public static string FixFormat(ReviewerDefinition reviewer, IReadOnlyList<string> errors) =>
        "Tu respuesta anterior no cumple el formato requerido (" + string.Join("; ", errors) + "). " +
        "Repetí el mismo contenido respetando exactamente este formato.\n\n" +
        ReviewStandard.ResponseFormat(reviewer.Name, reviewer.IdPrefix);

    /// <summary>Metadata file handed to every reviewer.</summary>
    public static string PrFile(PullRequest pr, IReadOnlyList<PrCommit> commits)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(pr.Key).Append(" — ").Append(pr.Title).Append("\n\n");
        sb.Append("- URL: ").Append(pr.Url).Append('\n');
        sb.Append("- Autor: ").Append(pr.Author).Append('\n');
        sb.Append("- Estado: ").Append(pr.State).Append('\n');
        sb.Append("- Ramas: ").Append(pr.SourceBranch).Append(" → ").Append(pr.DestinationBranch).Append("\n\n");
        sb.Append("## Descripción\n\n").Append(pr.Description.Trim().Length > 0 ? pr.Description.Trim() : "(sin descripción)").Append("\n\n");
        if (commits.Count > 0)
        {
            sb.Append("## Commits (más nuevo primero)\n\n");
            foreach (var c in commits)
                sb.Append("- ").Append(c.ShortHash).Append(' ').Append(c.Subject).Append('\n');
        }
        return sb.ToString();
    }

    private static void AppendMaterial(StringBuilder sb, ReviewerDefinition reviewer, PullRequest pr, RunContext context, bool hasPrevious)
    {
        sb.Append("## Material\n");
        if (context.HasDiff)
            sb.Append("- Diff del PR: `").Append(context.DiffPath).Append("`\n");
        else
            sb.Append("- No se pudo obtener el diff desde Bitbucket: usá `git fetch origin && git diff origin/")
                .Append(pr.DestinationBranch).Append("...origin/").Append(pr.SourceBranch).Append("`.\n");
        sb.Append("- Descripción, metadatos y commits: `").Append(context.PrPath).Append("`\n");
        if (context.HasProjectContext)
            sb.Append("- Contexto del proyecto (leelo antes de revisar): `").Append(context.ContextPath).Append("`\n");
        foreach (var file in context.ContextFiles)
            sb.Append("- Documentación del proyecto: `").Append(file).Append("` (relativo al repositorio)\n");
        if (hasPrevious)
            sb.Append("- Tus hallazgos anteriores: `").Append(context.PreviousPath(reviewer.Name)).Append("`\n");
        if (context.HasComments)
            sb.Append(CommentsLine(context.CommentsPath));
        if (context.Checkout is { Kind: CheckoutKind.Managed } managed)
        {
            sb.Append("- El directorio actual es el proyecto en la rama de destino `").Append(managed.BaseBranch).Append("` (commit ")
                .Append(CommitHashes.Short(managed.BaseCommit)).Append("), tal como está hoy. El diff muestra los cambios que el PR propone sobre esa base: ")
                .Append("leé el código que necesites para juzgar su impacto. Para comparar podés usar `git diff origin/")
                .Append(managed.BaseBranch).Append("...origin/").Append(pr.SourceBranch).Append("`.\n");
        }
        else
        {
            sb.Append(context.HasCheckout
                ? "- El directorio actual es el repositorio: leé el código que necesites para entender el cambio.\n"
                : "- No hay checkout del repositorio: trabajá con el diff.\n");
        }
        sb.Append("No modifiques archivos.\n\n");
    }

    /// <summary>Points the reviewer to the comments people already left on the PR.</summary>
    internal static string CommentsLine(string path) =>
        $"- Comentarios que ya dejaron personas en el PR: `{path}`. No repitas lo que ya está comentado; si un comentario señala " +
        "un problema de tu foco, verificá si se corrigió y, si sigue, reportalo citando el comentario.\n";

    /// <summary>Keeps user-written headings below the section that embeds them.</summary>
    private static string DemoteHeadings(string markdown) => Export.MemoText.NestHeadings(markdown.Trim(), 3);
}
