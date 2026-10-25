using System.CommandLine;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Review;
using Spectre.Console;

namespace Prt.Cli.Commands;

internal static class ReviewCommands
{
    public static Command Review()
    {
        var pr = new Argument<string>("pr") { Description = "URL del PR en Bitbucket o workspace/repo#numero" };
        var agent = AgentOption();
        var fresh = new Option<bool>("--new") { Description = "Abrir una sesión nueva aunque exista una para ese agente" };
        var noSummary = new Option<bool>("--no-summary") { Description = "No pedir el memo al agente al salir" };
        var noNote = new Option<bool>("--no-note") { Description = "No preguntar por una nota propia al salir" };
        var prompt = new Option<string?>("--prompt", "-p") { Description = "Instrucción extra para enviar al agente" };
        var reviewer = new Option<string?>("--reviewer", "-r") { Description = "Abrir la sesión de un revisor del panel (ej. security) para discutir sus hallazgos" };

        var command = new Command("review", "Lanza o retoma la sesión de revisión de un PR y registra la ronda")
        {
            pr, agent, fresh, noSummary, noNote, prompt, reviewer,
        };
        command.SetAction((result, ct) => Cli.Run(async app =>
        {
            var prRef = PrRef.Parse(result.GetValue(pr)!);
            var reviewerName = result.GetValue(reviewer)?.Trim().ToLowerInvariant();
            if (reviewerName is not null && result.GetValue(fresh))
                throw new UserException("--new no se combina con --reviewer: las sesiones de revisores las crea `prt analyze`.");
            var options = new ReviewOptions(
                ChooseAgent(app, prRef, result.GetValue(agent), reviewerName),
                ForceNewSession: result.GetValue(fresh),
                Summary: !result.GetValue(noSummary),
                AskNote: !result.GetValue(noNote),
                ExtraPrompt: result.GetValue(prompt),
                Reviewer: reviewerName);
            await RunReview(app, prRef, options, ct);
        }));
        return command;
    }

    public static Command Resume()
    {
        var agent = AgentOption();
        var command = new Command("resume", "Elegí un PR registrado y retomá su sesión") { agent };
        command.SetAction((result, ct) => Cli.Run(async app =>
        {
            var pr = app.Pick("¿Qué PR retomás?")
                ?? throw new UserException("No hay PRs registrados todavía. Empezá con `prt review <url>`.");
            await RunReview(app, pr.Ref, new ReviewOptions(ChooseAgent(app, pr.Ref, result.GetValue(agent))), ct);
        }));
        return command;
    }

    public static Option<string?> AgentOption()
    {
        var option = new Option<string?>("--agent", "-a") { Description = $"Agente: {string.Join(", ", AgentNames.All)}" };
        option.AcceptOnlyFromAmong(AgentNames.All);
        return option;
    }

    /// <summary>Explicit choice, else the agent last used on the PR (to keep the same session), else the default.</summary>
    private static string ChooseAgent(App app, PrRef prRef, string? requested, string? reviewer = null)
    {
        if (requested is not null)
            return requested;
        if (app.Store.FindPullRequest(prRef) is { } existing && app.Store.GetLatestSession(existing.Id, reviewer) is { } latest)
            return latest.Agent;
        return app.Config.DefaultAgent;
    }

    private static async Task RunReview(App app, PrRef prRef, ReviewOptions options, CancellationToken ct)
    {
        app.WarnIfNoVault();
        var outcome = await app.Review.ReviewAsync(prRef, options, ct);

        AnsiConsole.WriteLine();
        app.Ui.Success($"Ronda registrada para {outcome.Pr.Key} (sesión {outcome.Session.Agent} {outcome.Session.AgentSessionId}).");
        if (outcome.Pr.Verdict is { Length: > 0 } verdict)
            app.Ui.Info($"Veredicto: {verdict}");
        if (outcome.NotePath is not null)
            app.Ui.Info($"Nota: {outcome.NotePath}");
    }
}
