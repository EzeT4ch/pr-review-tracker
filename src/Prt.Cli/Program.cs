using System.CommandLine;
using Prt.Cli.Commands;
using Spectre.Console;

// Without a terminal the detected width can be 0, which collapses every table and line.
if (Console.IsOutputRedirected || AnsiConsole.Profile.Width < 40)
    AnsiConsole.Profile.Width = 160;

var root = new RootCommand(
    "prt — trazabilidad de revisiones de PRs de Bitbucket hechas con agentes CLI (Claude Code, Codex, opencode). " +
    "Guarda la sesión de cada PR para retomarla y exporta el historial a Obsidian.")
{
    SetupCommands.Init(),
    SetupCommands.Map(),
    ReviewCommands.Review(),
    ReviewCommands.Resume(),
    PanelCommands.Analyze(),
    PanelCommands.Report(),
    PanelCommands.Reviewers(),
    HistoryCommands.List(),
    HistoryCommands.Show(),
    RecordCommands.Memo(),
    RecordCommands.Link(),
    RecordCommands.Tag(),
    SyncCommands.Sync(),
    SyncCommands.Export(),
};

return await root.Parse(args).InvokeAsync();
