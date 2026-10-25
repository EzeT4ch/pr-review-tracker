using System.CommandLine;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;

namespace Prt.Cli.Commands;

internal static class SyncCommands
{
    public static Command Sync()
    {
        var pr = new Argument<string?>("pr") { Description = "PR a refrescar (por defecto, todos los abiertos)", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("sync", "Actualiza título, estado y ramas desde Bitbucket y regenera las notas") { pr };
        command.SetAction((result, ct) => Cli.Run(async app =>
        {
            IReadOnlyList<PrRef> targets = result.GetValue(pr) is { } one
                ? [PrRef.Parse(one)]
                : app.Store.ListPullRequests("OPEN").Select(p => p.Ref).ToList();
            if (targets.Count == 0)
            {
                app.Ui.Info("No hay PRs abiertos registrados.");
                return;
            }

            var review = app.Review;
            foreach (var target in targets)
            {
                try
                {
                    var (refreshed, _) = await review.RefreshAsync(target, ct);
                    app.Ui.Success($"{refreshed.Key}: {refreshed.State} · {refreshed.Title}");
                    if (app.Exporter is { } exporter)
                        exporter.ExportPr(app.Store.GetHistory(refreshed.Id));
                }
                catch (Exception ex) when (ex is BitbucketException or HttpRequestException)
                {
                    app.Ui.Warn($"{target.Key}: {ex.Message}");
                }
            }
            review.ExportIndex();
        }));
        return command;
    }

    public static Command Export()
    {
        var pr = new Argument<string?>("pr") { Description = "PR a exportar (por defecto, todos)", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("export", "Regenera las notas de Obsidian desde la base local") { pr };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var exporter = app.Exporter ?? throw new UserException("No hay vault configurado. Corré `prt init --vault <ruta>`.");
            var targets = result.GetValue(pr) is { } one
                ? [app.RequireTracked(one)]
                : app.Store.ListPullRequests();
            foreach (var target in targets)
                exporter.ExportPr(app.Store.GetHistory(target.Id));
            app.Review.ExportIndex();
            app.Ui.Success($"{targets.Count} nota(s) exportada(s) a {exporter.RootDir}");
            return Task.CompletedTask;
        }));
        return command;
    }
}
