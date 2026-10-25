namespace Prt.Core.Config;

/// <summary>
/// Prompt templates. Placeholders: {key} {url} {title} {author} {source} {destination}
/// {description}; for re-reviews also {count} {fromCommit} {toCommit} {commits}.
/// </summary>
public static class DefaultPrompts
{
    public const string Initial =
        """
        Revisá el Pull Request {key} de Bitbucket: {url}
        Título: {title}
        Autor: {author}
        Ramas: {source} -> {destination}

        Descripción del PR:
        {description}

        Obtené el diff con `git diff origin/{destination}...origin/{source}` (si falta alguna rama, `git fetch origin` antes).
        Revisá correctitud, diseño y arquitectura, impacto de negocio, seguridad, performance y tests.
        Priorizá los hallazgos (bloqueante / importante / menor) e indicá archivo y línea cuando aplique.
        Voy a hacer después una revisión de arquitectura y negocio, así que señalá explícitamente
        las decisiones que necesitan criterio humano.
        """;

    public const string ReReview =
        """
        El PR {key} recibió {count} commit(s) nuevo(s) desde la última revisión ({fromCommit} -> {toCommit}):
        {commits}

        Revisá solo lo que cambió desde entonces: indicá qué pendientes anteriores quedaron resueltos,
        cuáles siguen abiertos y si aparecieron problemas nuevos.
        """;

    public const string Summary =
        """
        Escribí un memo de esta sesión de revisión para mi registro. Respondé solo con el memo en Markdown,
        sin preámbulo, usando exactamente estas secciones:

        ## Resumen
        ## Hallazgos
        (una tabla Markdown con exactamente estas columnas:
        `| ID | Sev. | Hallazgo | Ubicación | Acción |`
        - ID: B1, B2… para bloqueantes, I1… para importantes, M1… para menores; Sev.: 🔴, 🟠 o 🟡;
        - Hallazgo: una oración; Ubicación: `Archivo.cs:línea` en inline code, o vacía; Acción: hasta 10 palabras;
        - una fila por hallazgo, sin saltos de línea ni listas dentro de una celda, y `|` escrito como `\|`)
        ## Riesgos de arquitectura
        ## Impacto de negocio
        ## Pendientes
        (lista de casillas `- [ ]`, una por cosa a resolver o verificar)

        Terminá con una única línea: `Veredicto: APROBAR | APROBAR CON CAMBIOS | CAMBIOS REQUERIDOS | BLOQUEADO`
        """;
}
