using System.CommandLine;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;

namespace Prt.Cli.Commands;

internal static class RecordCommands
{
    public static Command Memo()
    {
        var pr = HistoryCommands.PrArgument();
        var message = new Option<string?>("--message", "-m") { Description = "Texto del memo (si se omite, abre $EDITOR o entrada en línea)" };
        var kind = new Option<string>("--kind", "-k")
        {
            Description = "note (nota) o decision (decisión de arquitectura/negocio)",
            DefaultValueFactory = _ => "note",
        };
        kind.AcceptOnlyFromAmong("note", "decision");

        var command = new Command("memo", "Agrega un memo a un PR sin abrir el agente") { pr, message, kind };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var target = app.RequireTracked(result.GetValue(pr));
            var text = result.GetValue(message) ?? NoteEditor.Capture();
            if (string.IsNullOrWhiteSpace(text))
                throw new UserException("Memo vacío; no se guardó nada.");

            var memoKind = result.GetValue(kind) == "decision" ? MemoKinds.Decision : MemoKinds.UserNote;
            app.Store.AddMemo(target.Id, null, memoKind, text);
            app.Ui.Success($"{MemoKinds.Label(memoKind)} agregada a {target.Key}.");
            Cli.ExportAndReport(app, target.Id);
            return Task.CompletedTask;
        }));
        return command;
    }

    public static Command Link()
    {
        var pr = new Argument<string>("pr") { Description = "URL del PR o workspace/repo#numero" };
        var agent = ReviewCommands.AgentOption();
        agent.Required = true;
        var session = new Option<string>("--session", "-s") { Description = "Id de la sesión del agente", Required = true };
        var cwd = new Option<string?>("--cwd") { Description = "Directorio donde corre la sesión (por defecto, el actual)" };

        var command = new Command("link", "Asocia una sesión existente de un agente a un PR") { pr, agent, session, cwd };
        command.SetAction((result, ct) => Cli.Run(async app =>
        {
            var prRef = PrRef.Parse(result.GetValue(pr)!);
            PullRequest target;
            try
            {
                (target, _) = await app.Review.RefreshAsync(prRef, ct);
            }
            catch (Exception ex) when (ex is BitbucketException or HttpRequestException)
            {
                app.Ui.Warn($"No se pudo consultar Bitbucket ({ex.Message}); se registra el PR sin metadatos.");
                target = app.Store.UpsertPullRequest(new PullRequest
                {
                    Workspace = prRef.Workspace, Repo = prRef.Repo, Number = prRef.Number, Url = prRef.WebUrl,
                });
            }

            var directory = Path.GetFullPath(result.GetValue(cwd) ?? app.Config.RepoPathFor(prRef.RepoKey) ?? Directory.GetCurrentDirectory());
            var linked = app.Store.AddSession(target.Id, result.GetValue(agent)!, result.GetValue(session)!.Trim(), directory);
            app.Ui.Success($"Sesión {linked.Agent} {linked.AgentSessionId} asociada a {target.Key}; `prt review {target.Key}` la retoma.");
            Cli.ExportAndReport(app, target.Id);
        }));
        return command;
    }

    public static Command Tag()
    {
        var pr = new Argument<string>("pr") { Description = "URL del PR o workspace/repo#numero" };
        var tags = new Argument<string[]>("tags") { Description = "Tags (ej. arquitectura pagos)", Arity = ArgumentArity.OneOrMore };
        var remove = new Option<bool>("--remove") { Description = "Quitar los tags en lugar de agregarlos" };

        var command = new Command("tag", "Agrega o quita tags de Obsidian a un PR") { pr, tags, remove };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var target = app.RequireTracked(result.GetValue(pr));
            if (result.GetValue(remove))
                app.Store.RemoveTags(target.Id, result.GetValue(tags)!);
            else
                app.Store.AddTags(target.Id, result.GetValue(tags)!);
            app.Ui.Success($"Tags de {target.Key}: {string.Join(", ", app.Store.ListTags(target.Id))}");
            Cli.ExportAndReport(app, target.Id);
            return Task.CompletedTask;
        }));
        return command;
    }
}
