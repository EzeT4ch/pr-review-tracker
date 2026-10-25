using Prt.Core.Panel;
using Spectre.Console;

namespace Prt.Cli;

/// <summary>
/// Terminal symbols with plain-text fallbacks. The classic Windows console (conhost) has no glyphs for
/// emojis or check marks even with UTF-8 output; Windows Terminal, VS Code and Unix terminals do.
/// </summary>
internal static class Symbols
{
    public static readonly bool Fancy = Detect();

    public static string Ok => Fancy ? "[green]✓[/]" : "[green]OK[/]";
    public static string Fail => Fancy ? "[red]✗[/]" : "[red]X[/]";
    public static string Bullet => Fancy ? "[grey]›[/]" : "[grey]>[/]";
    public static string Warning => "[yellow]![/]";
    public static string Question => "[blue]?[/]";

    /// <summary>Rounded corners are missing from the classic console's fonts; square ones are not.</summary>
    public static TableBorder Border => Fancy ? TableBorder.Rounded : TableBorder.Square;

    /// <summary>Severity marker, as markup.</summary>
    public static string Severity(Severity severity) => Fancy
        ? ReviewStandard.Icon(severity)
        : severity switch
        {
            Prt.Core.Panel.Severity.Blocker => "[red]BLOQ[/]",
            Prt.Core.Panel.Severity.Major => "[darkorange]IMP[/]",
            Prt.Core.Panel.Severity.Minor => "[yellow]MEN[/]",
            _ => "[blue]INFO[/]",
        };

    private static bool Detect()
    {
        if (Environment.GetEnvironmentVariable("PRT_ASCII") is { Length: > 0 } ascii && ascii != "0")
            return false;
        if (!AnsiConsole.Profile.Capabilities.Unicode)
            return false;
        return !OperatingSystem.IsWindows()
               || Environment.GetEnvironmentVariable("WT_SESSION") is { Length: > 0 }
               || Environment.GetEnvironmentVariable("TERM_PROGRAM") is { Length: > 0 };
    }
}
