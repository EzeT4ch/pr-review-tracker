using Prt.Core.Agents;
using Prt.Core.Bitbucket;
using Spectre.Console;

namespace Prt.Cli;

internal static class Cli
{
    /// <summary>Runs a command body with a fresh <see cref="App"/>, turning known failures into messages.</summary>
    public static async Task<int> Run(Func<App, Task> body)
    {
        try
        {
            using var app = App.Load();
            await body(app);
            return 0;
        }
        catch (Exception ex) when (ex is UserException or FormatException or AgentException or BitbucketException
                                       or ArgumentException or InvalidOperationException or HttpRequestException)
        {
            AnsiConsole.MarkupLine($"{Symbols.Fail} {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }

    public static void ExportAndReport(App app, long prId)
    {
        if (app.Review.Export(prId) is { } path)
            app.Ui.Info($"Nota: {path}");
        else
            app.WarnIfNoVault();
    }
}
