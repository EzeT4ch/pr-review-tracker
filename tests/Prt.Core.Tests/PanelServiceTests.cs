using System.Text.Json;
using System.Text.RegularExpressions;
using Prt.Core.Agents;
using Prt.Core.Bitbucket;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Export;
using Prt.Core.Panel;
using Prt.Core.Repos;
using Prt.Core.Review;
using Prt.Core.Storage;

namespace Prt.Core.Tests;

public sealed class PanelServiceTests : IDisposable
{
    private static readonly PrRef Ref = new("acme", "api", 42);

    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteStore _store;
    private readonly FakeRunner _runner = new();
    private readonly FakeBitbucket _bitbucket = new();
    private readonly FakeUi _ui = new();
    private readonly PrtConfig _config = new();
    private readonly ObsidianExporter _exporter;
    private readonly string _checkout;

    /// <summary>reviewer → answer the fake agent gives (the report JSON, or anything else).</summary>
    private readonly Dictionary<string, Func<int, string>> _answers = new();

    public PanelServiceTests()
    {
        _store = new SqliteStore(":memory:", _clock);
        _checkout = Directory.CreateDirectory(Path.Combine(_dir.Path, "checkout")).FullName;
        _config.VaultPath = Path.Combine(_dir.Path, "vault");
        _config.RepoPaths["acme/api"] = _checkout;
        _bitbucket.Commits = [new PrCommit("c1full", "inicial", null)];
        _exporter = new ObsidianExporter(_config.VaultPath, _config.VaultFolder, (a, id) => $"{a} {id}", TimeZoneInfo.Utc);

        var calls = new Dictionary<string, int>();
        _runner.OnCaptured = args =>
        {
            var prompt = args[^1];
            var reviewer = Regex.Match(prompt, "\"reviewer\": \"(\\w+)\"").Groups[1].Value;
            int call;
            lock (calls)
                call = calls[reviewer] = calls.GetValueOrDefault(reviewer) + 1;
            var answer = _answers.TryGetValue(reviewer, out var a) ? a(call) : Report(reviewer, "approve");
            return answer.StartsWith("EXIT:")
                ? new ProcessResult(1, "", answer[5..])
                : new ProcessResult(0, JsonSerializer.Serialize(new { type = "result", is_error = false, result = answer }), "");
        };
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private PanelService Service(ReviewProfile? profile = null, ICheckoutProvider? checkouts = null) => new(
        _config, _store, _bitbucket, name => AgentFactory.Create(name, _config, _runner), _exporter, _ui,
        Path.Combine(_dir.Path, "runs"), _clock, (_, _) => profile ?? ProfileLoader.Load([]), checkouts);

    private sealed class FixedCheckout(CheckoutInfo info) : ICheckoutProvider
    {
        public Task<CheckoutInfo?> ResolveAsync(PullRequest pr, IStatusUi ui, CancellationToken ct = default) =>
            Task.FromResult<CheckoutInfo?>(info);
    }

    [Fact]
    public async Task Managed_clone_is_the_working_directory_and_the_base_is_recorded()
    {
        var clone = Directory.CreateDirectory(Path.Combine(_dir.Path, "clone")).FullName;
        _config.RepoPaths.Clear();

        var outcome = await Service(checkouts: new FixedCheckout(new CheckoutInfo(clone, CheckoutKind.Managed, "develop", "0123456789abcdef")))
            .AnalyzeAsync(Ref, new PanelOptions(Reviewers: ["bugs"]));

        var call = _runner.Captured.Single();
        Assert.Equal(clone, call.Cwd);
        Assert.Contains("rama de destino `develop` (commit 0123456789ab), tal como está hoy", call.Args[^1]);
        Assert.Contains("git diff origin/develop...origin/feature/qr", call.Args[^1]);
        Assert.Equal(("develop", "0123456789abcdef"), (outcome.Run.BaseBranch, outcome.Run.BaseCommit));
        var report = Directory.GetFiles(Path.GetDirectoryName(outcome.NotePath)!, "Análisis*.md").Single();
        Assert.Contains("base `develop` @ `0123456789ab` (clon gestionado)", File.ReadAllText(report));
    }

    private static string Report(string reviewer, string verdict, params string[] findings) =>
        $$"""{"reviewer":"{{reviewer}}","verdict":"{{verdict}}","summary":"Resumen de {{reviewer}}.","findings":[{{string.Join(",", findings)}}],"questions":[]}""";

    private static string Finding(string id, string severity, string status = "new") =>
        $$"""{"id":"{{id}}","severity":"{{severity}}","title":"Hallazgo {{id}}","file":"src/Pay.cs","line":12,"description":"d","recommendation":"r","confidence":"high","status":"{{status}}"}""";

    [Fact]
    public async Task Reviewers_read_the_comments_people_left_on_the_pr()
    {
        _bitbucket.Comments =
        [
            new PrComment(1, null, "Bruno", "Esto rompe con ids negativos.", new DateTimeOffset(2026, 3, 9, 10, 0, 0, TimeSpan.Zero), "src/Pay.cs", null, 12, ""),
            new PrComment(2, 1, "Ana", "Lo arreglo.\nEn el próximo commit.", new DateTimeOffset(2026, 3, 9, 11, 0, 0, TimeSpan.Zero), "src/Pay.cs", null, 12, ""),
            new PrComment(3, null, "Bruno", "¿Hay tests?", new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero), null, null, null, ""),
        ];

        await Service().AnalyzeAsync(Ref, new PanelOptions(Reviewers: ["bugs"]));

        var call = _runner.Captured.Single();
        var contextDir = call.Args[call.Args.IndexOf("--add-dir") + 1];
        var file = File.ReadAllText(Path.Combine(contextDir, "comments.md"));
        Assert.Contains("## `src/Pay.cs`\n\n### Línea 12 (nueva)\n- **Bruno** (2026-03-09): Esto rompe con ids negativos.\n  - **Ana** (2026-03-09): Lo arreglo.\n    En el próximo commit.\n", file);
        Assert.Contains("## Comentarios generales", file);
        Assert.Contains(Path.Combine(contextDir, "comments.md"), call.Args[^1]);
        Assert.Contains("No repitas lo que ya está comentado", call.Args[^1]);

        // Without comments (or when they cannot be read) the prompt does not mention them.
        _bitbucket.Comments = null;
        await Service().AnalyzeAsync(Ref, new PanelOptions(Reviewers: ["bugs"], ForceNewSessions: true));
        Assert.DoesNotContain("comments.md", _runner.Captured[^1].Args[^1]);
        Assert.Contains(_ui.Messages, m => m.Contains("no se pudieron leer los comentarios"));
    }

    [Fact]
    public async Task Runs_every_reviewer_in_its_own_session_and_consolidates()
    {
        _answers["security"] = _ => Report("security", "request_changes", Finding("SEC-1", "major"));
        _answers["bugs"] = _ => Report("bugs", "approve_with_comments", Finding("BUG-1", "minor"));

        var outcome = await Service().AnalyzeAsync(Ref, new PanelOptions());

        Assert.Equal(4, outcome.Reviewers.Count);
        Assert.All(outcome.Reviewers, r => Assert.Equal(ResultStatus.Ok, r.Result.Status));
        Assert.Equal(Verdict.RequestChanges, outcome.Verdict);
        Assert.Equal("CAMBIOS REQUERIDOS", outcome.Pr.Verdict);
        Assert.Equal("CAMBIOS REQUERIDOS", outcome.Run.Verdict);

        var sessions = _store.ListSessions(outcome.Pr.Id);
        Assert.Equal(["architecture", "bugs", "performance", "security"], sessions.Select(s => s.Reviewer).Order());
        Assert.All(sessions, s => Assert.Equal(_checkout, s.Cwd));

        var call = _runner.Captured.First();
        var contextDir = call.Args[call.Args.IndexOf("--add-dir") + 1];
        Assert.Equal("--session-id", call.Args[call.Args.IndexOf("--add-dir") + 2]);
        Assert.True(File.Exists(Path.Combine(contextDir, "diff.patch")));
        Assert.Contains("Agregar pagos con QR", File.ReadAllText(Path.Combine(contextDir, "pr.md")));
        Assert.Contains(Path.Combine(contextDir, "diff.patch"), call.Args[^1]);
        Assert.Contains("## Estándar de revisión", call.Args[^1]);

        var note = File.ReadAllText(outcome.NotePath!);
        Assert.Contains("## Panel de revisores", note);
        Assert.Contains("| ID | Sev. | Hallazgo | Ubicación | Acción |\n", note);
        Assert.Contains(")#^sec-1\\|SEC-1]] | 🟠 | Hallazgo SEC-1 | `Pay.cs:12` | r |\n", note);
        Assert.Contains("panel_verdict: \"CAMBIOS REQUERIDOS\"", note);
        var prFolder = Path.Combine(_config.VaultPath!, "PR Reviews", "acme", "api", "PR-42");
        Assert.Equal(prFolder, Path.GetDirectoryName(outcome.NotePath));
        var runNote = Directory.GetFiles(prFolder, "Análisis*.md").Single();
        Assert.Contains("PR: [[PR Reviews/acme/api/PR-42/PR-42 - Agregar pagos con QR|", File.ReadAllText(runNote));
        var report = File.ReadAllText(runNote);
        Assert.Contains("## Resultado: **CAMBIOS REQUERIDOS**", report);
        Assert.Contains("| [[#^sec-1\\|SEC-1]] | 🟠 | Hallazgo SEC-1 | `Pay.cs:12` | r | nuevo |\n", report);
        Assert.Contains("### 🟠 SEC-1 · Hallazgo SEC-1", report);
        Assert.Contains("**Ubicación:** `src/Pay.cs:12` · **Confianza:** alta · **Estado:** nuevo ^sec-1\n", report);
        Assert.Contains("| bugs | claude | APROBAR CON CAMBIOS | 0 | 0 | 1 | 0 | 0 |", report);
        // Reviewers are listed in profile order, whatever order they finished in.
        Assert.Equal(["architecture", "bugs", "performance", "security"], outcome.Reviewers.Select(r => r.Reviewer.Name));
        Assert.Equal(["architecture", "bugs", "performance", "security"],
            _store.ListRuns(outcome.Pr.Id).Single().Results.Select(r => r.Reviewer));
    }

    [Fact]
    public async Task Empty_pick_runs_every_reviewer_only_when_asked()
    {
        var layer = Directory.CreateDirectory(Path.Combine(_dir.Path, "layer")).FullName;
        Directory.CreateDirectory(Path.Combine(layer, "reviewers"));
        File.WriteAllText(Path.Combine(layer, "reviewers", "security.md"), "---\nenabled: false\n---\nfoco");
        var profile = ProfileLoader.Load([layer]);

        var enabled = await Service(profile).AnalyzeAsync(Ref, new PanelOptions());
        var all = await Service(profile).AnalyzeAsync(Ref, new PanelOptions(AllWhenEmpty: true));

        Assert.Equal(["architecture", "bugs", "performance"], enabled.Reviewers.Select(r => r.Reviewer.Name));
        Assert.Equal(["architecture", "bugs", "performance", "security"], all.Reviewers.Select(r => r.Reviewer.Name));
    }

    [Fact]
    public async Task Second_run_resumes_each_reviewer_with_its_previous_findings()
    {
        _answers["security"] = call => call == 1
            ? Report("security", "request_changes", Finding("SEC-1", "major"))
            : Report("security", "approve", Finding("SEC-1", "major", "resolved"));
        var first = await Service().AnalyzeAsync(Ref, new PanelOptions(Reviewers: ["security"]));
        _bitbucket.Commits = [new PrCommit("c2full", "arregla auth", null), .. _bitbucket.Commits];

        var second = await Service().AnalyzeAsync(Ref, new PanelOptions(Reviewers: ["security"]));

        var session = _store.ListSessions(first.Pr.Id).Single();
        var resume = _runner.Captured[1].Args;
        Assert.Equal(session.AgentSessionId, resume[resume.IndexOf("--resume") + 1]);
        var prompt = resume[^1];
        Assert.Contains("c2full arregla auth", prompt);
        // The current focus is restated, so edits to a reviewer reach resumed sessions.
        Assert.Contains("## Tu foco (vigente)", prompt);
        var previous = Regex.Match(prompt, @"Tus hallazgos anteriores: `([^`]+)`").Groups[1].Value;
        Assert.Contains("SEC-1", File.ReadAllText(previous));

        Assert.Equal(Verdict.Approve, second.Verdict);
        Assert.Equal("APROBAR", second.Pr.Verdict);
        var note = File.ReadAllText(second.NotePath!);
        Assert.Contains("Análisis anteriores:", note);
        // The PR folder holds the main note plus one report per run.
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_config.VaultPath!, "PR Reviews", "acme", "api", "PR-42")).Length);
    }

    [Fact]
    public async Task Off_format_answers_get_one_correction_round()
    {
        _answers["bugs"] = call => call == 1 ? "Encontré un bug en Pay.cs, revisalo." : Report("bugs", "approve");
        _answers["security"] = _ => "No hay problemas de seguridad.";

        var outcome = await Service().AnalyzeAsync(Ref, new PanelOptions(Reviewers: ["bugs", "security"]));

        var bugs = outcome.Reviewers.Single(r => r.Reviewer.Name == "bugs");
        Assert.Equal(ResultStatus.Ok, bugs.Result.Status);
        Assert.Contains("reintento de formato", bugs.Result.RawOutput);
        var security = outcome.Reviewers.Single(r => r.Reviewer.Name == "security");
        Assert.Equal(ResultStatus.Invalid, security.Result.Status);
        Assert.Contains("JSON", security.Result.Error);
        Assert.Contains(_runner.Captured, c => c.Args[^1].StartsWith("Tu respuesta anterior no cumple el formato"));
        Assert.Equal(Verdict.Approve, outcome.Verdict);
    }

    [Fact]
    public async Task A_failing_agent_does_not_stop_the_panel()
    {
        _answers["performance"] = _ => "EXIT:rate limit";

        var outcome = await Service().AnalyzeAsync(Ref, new PanelOptions());

        var failed = outcome.Reviewers.Single(r => r.Result.Status == ResultStatus.Failed);
        Assert.Equal("performance", failed.Reviewer.Name);
        Assert.Contains("rate limit", failed.Result.Error);
        Assert.Null(failed.Result.SessionId);
        Assert.Equal(3, _store.ListSessions(outcome.Pr.Id).Count);
        Assert.Contains("Sin reporte válido: performance", File.ReadAllText(outcome.NotePath!));
    }

    [Fact]
    public async Task Project_profile_decides_reviewers_and_agents()
    {
        var prt = Path.Combine(_checkout, ".prt");
        Directory.CreateDirectory(Path.Combine(prt, "reviewers"));
        File.WriteAllText(Path.Combine(prt, "review.json"), """{ "reviewers": ["security"], "agents": { "security": "codex" } }""");
        File.WriteAllText(Path.Combine(prt, "context.md"), "Procesamos pagos con tarjeta.");
        var profile = ProfileLoader.Load([prt]);
        _runner.OnCaptured = args => new ProcessResult(0, string.Join('\n',
            """{"type":"thread.started","thread_id":"thr-123"}""",
            JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = Report("security", "approve") } })), "");

        var outcome = await Service(profile).AnalyzeAsync(Ref, new PanelOptions());

        var call = _runner.Captured.Single();
        Assert.Equal("codex", call.Command);
        Assert.Equal(["exec", "--json", "--sandbox", "read-only", "--skip-git-repo-check"], call.Args[..5]);
        Assert.Contains("context.md", call.Args[^1]);
        Assert.Equal("thr-123", _store.ListSessions(outcome.Pr.Id).Single().AgentSessionId);
    }

    [Fact]
    public async Task Missing_diff_tells_reviewers_to_use_git()
    {
        _bitbucket.Diff = null;

        await Service().AnalyzeAsync(Ref, new PanelOptions(Reviewers: ["bugs"]));

        Assert.Contains("git diff origin/main...origin/feature/qr", _runner.Captured.Single().Args[^1]);
        Assert.Contains(_ui.Messages, m => m.StartsWith("warn: No se pudo obtener el diff"));
    }

    [Fact]
    public async Task Reviewer_sessions_do_not_replace_the_interactive_review_session()
    {
        await Service().AnalyzeAsync(Ref, new PanelOptions(Reviewers: ["bugs"]));
        var review = new ReviewService(_config, _store, _bitbucket, name => AgentFactory.Create(name, _config, _runner), _exporter, _ui, _clock);

        var outcome = await review.ReviewAsync(Ref, new ReviewOptions("claude", Summary: false, AskNote: false));
        var discuss = await review.ReviewAsync(Ref, new ReviewOptions("claude", Summary: false, AskNote: false, Reviewer: "bugs"));

        Assert.Equal(RoundKinds.New, outcome.Round.Kind);
        Assert.Null(outcome.Session.Reviewer);
        Assert.Equal("bugs", discuss.Session.Reviewer);
        Assert.Equal(RoundKinds.Resume, discuss.Round.Kind);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            review.ReviewAsync(Ref, new ReviewOptions("claude", Summary: false, AskNote: false, Reviewer: "security")));
    }

    [Fact]
    public async Task Unknown_reviewer_is_rejected_before_running_agents()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().AnalyzeAsync(Ref, new PanelOptions(Reviewers: ["ux"])));
        Assert.Empty(_runner.Captured);
    }
}
