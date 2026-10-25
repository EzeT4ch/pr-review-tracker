using System.CommandLine;
using System.Globalization;
using Prt.Core.Bitbucket;
using Prt.Core.Domain;
using Spectre.Console;

namespace Prt.Cli.Commands;

internal static class HistoryCommands
{
    public static Command List()
    {
        var state = new Option<string?>("--state", "-s") { Description = "Filtrar por estado de Bitbucket (OPEN, MERGED, DECLINED…)" };
        var repo = new Option<string?>("--repo", "-r") { Description = "Filtrar por workspace/repo" };
        var command = new Command("list", "Lista los PRs registrados, del más reciente al más viejo") { state, repo };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var prs = app.Store.ListPullRequests(result.GetValue(state), result.GetValue(repo));
            if (prs.Count == 0)
            {
                app.Ui.Info("No hay PRs registrados con ese filtro.");
                return Task.CompletedTask;
            }

            var table = new Table().Border(Symbols.Border)
                .AddColumns("PR", "Título", "Autor", "Estado", "Veredicto", "Sesiones", "Rondas", "Actividad");
            foreach (var pr in prs)
            {
                var history = app.Store.GetHistory(pr.Id);
                table.AddRow(
                    Markup.Escape(pr.Key),
                    Markup.Escape(pr.Title.Length > 50 ? pr.Title[..49] + "…" : pr.Title),
                    Markup.Escape(pr.Author),
                    Markup.Escape(pr.State),
                    Markup.Escape(pr.Verdict ?? ""),
                    string.Join(", ", history.Sessions.Select(s => s.Agent).Distinct()),
                    history.Rounds.Count.ToString(CultureInfo.InvariantCulture),
                    Local(app.Store.LastActivity(pr.Id)));
            }
            AnsiConsole.Write(table);
            return Task.CompletedTask;
        }));
        return command;
    }

    public static Command Show()
    {
        var pr = PrArgument();
        var command = new Command("show", "Muestra sesiones, rondas y memos de un PR") { pr };
        command.SetAction((result, _) => Cli.Run(app =>
        {
            var history = app.Store.GetHistory(app.RequireTracked(result.GetValue(pr)).Id);
            var p = history.Pr;

            AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(p.Key)}[/] {Markup.Escape(p.Title)}").LeftJustified());
            AnsiConsole.MarkupLine(Markup.Escape($"{p.Author} · {p.SourceBranch} → {p.DestinationBranch} · {p.State}"));
            if (p.Url.Length > 0)
                AnsiConsole.MarkupLine($"[link]{Markup.Escape(p.Url)}[/]");
            if (p.Verdict is { Length: > 0 })
                AnsiConsole.MarkupLine($"Veredicto: [bold]{Markup.Escape(p.Verdict)}[/]");
            if (history.Tags.Count > 0)
                AnsiConsole.MarkupLine($"Tags: {Markup.Escape(string.Join(", ", history.Tags))}");

            if (history.Sessions.Count > 0)
            {
                var sessions = new Table().Border(TableBorder.Simple).AddColumns("Agente", "Sesión", "Último uso", "Retomar");
                foreach (var s in history.Sessions)
                {
                    sessions.AddRow(
                        Markup.Escape(s.Agent + (s.IsDefault ? " *" : "")),
                        Markup.Escape(s.AgentSessionId),
                        Local(s.LastUsedAt),
                        Markup.Escape(app.ResumeCommand(s.Agent, s.AgentSessionId)));
                }
                AnsiConsole.Write(sessions);
            }

            var sessionsById = history.Sessions.ToDictionary(s => s.Id);
            var events = history.Rounds.Select(r => (At: r.StartedAt, Round: (Round?)r, Memo: (Memo?)null))
                .Concat(history.Memos.Select(m => (At: m.CreatedAt, Round: (Round?)null, Memo: (Memo?)m)))
                .OrderBy(e => e.At);
            foreach (var (at, round, memo) in events)
            {
                if (round is not null)
                {
                    var agent = sessionsById.TryGetValue(round.SessionId, out var s) ? s.Agent : "?";
                    var kind = round.Kind == RoundKinds.New ? "nueva sesión" : "retomada";
                    AnsiConsole.Write(new Rule(Markup.Escape(
                        $"{Local(at)} · {agent} · {kind} · {CommitHashes.Short(round.CommitAtStart)}")).LeftJustified().RuleStyle("grey"));
                }
                else if (memo is not null)
                {
                    AnsiConsole.Write(new Panel(Markup.Escape(memo.Body))
                        .Header(Markup.Escape($"{MemoKinds.Label(memo.Kind)} · {Local(memo.CreatedAt)}"))
                        .Border(BoxBorder.Rounded)
                        .Expand());
                }
            }

            if (app.Exporter is { } exporter)
                AnsiConsole.MarkupLine($"[grey]Nota: {Markup.Escape(Path.Combine(exporter.NoteDirectory(p), exporter.NoteFileName(p)))}[/]");
            return Task.CompletedTask;
        }));
        return command;
    }

    public static Argument<string?> PrArgument() => new("pr")
    {
        Description = "URL del PR o workspace/repo#numero (si se omite, se elige de la lista)",
        Arity = ArgumentArity.ZeroOrOne,
    };

    internal static string Local(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
