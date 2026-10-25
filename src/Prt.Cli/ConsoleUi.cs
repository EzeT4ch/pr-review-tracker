using System.Diagnostics;
using Prt.Core.Domain;
using Prt.Core.Panel;
using Prt.Core.Review;
using Spectre.Console;

namespace Prt.Cli;

internal sealed class ConsoleUi : IReviewUi, IPanelUi
{
    public static bool Interactive => !Console.IsInputRedirected;

    public void Info(string message) => AnsiConsole.MarkupLine($"{Symbols.Bullet} {Markup.Escape(message)}");

    public void Warn(string message) => AnsiConsole.MarkupLine($"{Symbols.Warning} {Markup.Escape(message)}");

    public void Success(string message) => AnsiConsole.MarkupLine($"{Symbols.Ok} {Markup.Escape(message)}");

    public bool Confirm(string question, bool defaultValue) =>
        Interactive ? AnsiConsole.Confirm(Markup.Escape(question), defaultValue) : defaultValue;

    public string? AskSessionId(string agent)
    {
        if (!Interactive)
            return null;
        var id = AnsiConsole.Prompt(
            new TextPrompt<string>($"Pegá el id de la sesión de {Markup.Escape(agent)} (vacío para cancelar):").AllowEmpty());
        return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    }

    public void ReviewerStarted(ReviewerDefinition reviewer, string agent, bool resumed) =>
        AnsiConsole.MarkupLine($"{Symbols.Bullet} {Markup.Escape(reviewer.Title)} [grey]({Markup.Escape(agent)}, {(resumed ? "sesión retomada" : "sesión nueva")})[/]");

    public void ReviewerFinished(ReviewerOutcome outcome)
    {
        var name = Markup.Escape(outcome.Reviewer.Title);
        if (outcome.Report is { } report)
        {
            var counts = string.Join(" ", new[] { Severity.Blocker, Severity.Major, Severity.Minor, Severity.Info }
                .Select(s => $"{Symbols.Severity(s)} {report.OpenFindings.Count(f => f.Severity == s)}"));
            AnsiConsole.MarkupLine($"{Symbols.Ok} {name}: {Markup.Escape(ReviewStandard.Label(report.Verdict))}  {counts}");
        }
        else
        {
            var what = outcome.Result.Status == ResultStatus.Invalid ? "respuesta fuera de formato" : "falló";
            AnsiConsole.MarkupLine($"{Symbols.Fail} {name}: {what}: {Markup.Escape(outcome.Result.Error ?? "")}");
        }
        foreach (var warning in outcome.Warnings)
            Warn($"{outcome.Reviewer.Name}: {warning}");
    }

    public string? AskNote() =>
        Interactive && AnsiConsole.Confirm("¿Agregar una nota tuya a esta ronda?", false) ? NoteEditor.Capture() : null;
}

/// <summary>Captures multi-line text with $VISUAL/$EDITOR, or inline in the terminal.</summary>
internal static class NoteEditor
{
    private const string Hint = "<!-- Escribí tu nota en Markdown. Esta línea se ignora. -->";

    public static string? Capture()
    {
        var editor = Environment.GetEnvironmentVariable("VISUAL") is { Length: > 0 } v ? v
            : Environment.GetEnvironmentVariable("EDITOR") is { Length: > 0 } e ? e : null;
        return editor is null ? CaptureInline() : CaptureWithEditor(editor);
    }

    private static string? CaptureInline()
    {
        AnsiConsole.MarkupLine("[grey]Escribí la nota (Markdown). Terminá con una línea que tenga solo un punto: [bold].[/][/]");
        var lines = new List<string>();
        while (Console.ReadLine() is { } line && line.Trim() != ".")
            lines.Add(line);
        var text = string.Join('\n', lines).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? CaptureWithEditor(string editor)
    {
        var file = Path.Combine(Path.GetTempPath(), $"prt-note-{Guid.NewGuid():N}.md");
        File.WriteAllText(file, Hint + "\n\n");
        try
        {
            // EDITOR may carry arguments, e.g. "code --wait".
            var parts = editor.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var info = new ProcessStartInfo(parts[0]) { UseShellExecute = false };
            foreach (var part in parts.Skip(1))
                info.ArgumentList.Add(part);
            info.ArgumentList.Add(file);
            using (var process = Process.Start(info))
                process?.WaitForExit();

            var text = File.ReadAllText(file).Replace(Hint, "").Trim();
            return text.Length == 0 ? null : text;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            AnsiConsole.MarkupLine($"[yellow]![/] No se pudo abrir el editor '{Markup.Escape(editor)}'.");
            return CaptureInline();
        }
        finally
        {
            File.Delete(file);
        }
    }
}
