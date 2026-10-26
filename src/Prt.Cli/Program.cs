using System.CommandLine;
using System.Text;
using Prt.Cli.Commands;
using Spectre.Console;

// The Windows console defaults to an OEM code page (accents come out as '?'). This must run before
// AnsiConsole is first used, because Spectre reads the encoding when it creates the console.
if (OperatingSystem.IsWindows())
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
}

// Before the first AnsiConsole access, which captures the encoding: the OEM code page (CP850) garbles accents.
Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// Without a terminal the detected width can be 0, which collapses every table and line.
if (Console.IsOutputRedirected || AnsiConsole.Profile.Width < 40)
    AnsiConsole.Profile.Width = 160;

var root = new RootCommand(
    "prt — trazabilidad de revisiones de PRs de Bitbucket hechas con agentes CLI (Claude Code, Codex, opencode). " +
    "Guarda la sesión de cada PR para retomarla y exporta el historial a Obsidian.")
{
    SetupCommands.Init(),
    SetupCommands.Map(),
    SetupCommands.Repos(),
    ReviewCommands.Review(),
    ReviewCommands.Resume(),
    PanelCommands.Analyze(),
    PanelCommands.Report(),
    PanelCommands.Reviewers(),
    GroupCommands.Group(),
    HistoryCommands.List(),
    HistoryCommands.Show(),
    RecordCommands.Memo(),
    RecordCommands.Link(),
    RecordCommands.Tag(),
    SyncCommands.Sync(),
    SyncCommands.Export(),
    SyncCommands.RepairText(),
    WebCommands.Web(),
};

return await root.Parse(args).InvokeAsync();
