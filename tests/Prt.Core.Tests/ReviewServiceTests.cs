using Prt.Core.Agents;
using Prt.Core.Bitbucket;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Export;
using Prt.Core.Review;
using Prt.Core.Storage;

namespace Prt.Core.Tests;

public sealed class ReviewServiceTests : IDisposable
{
    private static readonly PrRef Ref = new("acme", "api", 42);

    private readonly TempDir _dir = new();
    private readonly SqliteStore _store;
    private readonly FakeRunner _runner = new();
    private readonly FakeBitbucket _bitbucket = new();
    private readonly FakeUi _ui = new();
    private readonly PrtConfig _config = new();
    private readonly ReviewService _service;

    public ReviewServiceTests()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
        _store = new SqliteStore(":memory:", clock);
        _config.VaultPath = Path.Combine(_dir.Path, "vault");
        _config.RepoPaths["acme/api"] = _dir.Path;
        _runner.OnCaptured = _ => new ProcessResult(0,
            """{"result":"## Resumen\nBien.\n\n## Pendientes\n- [ ] test\n\nVeredicto: APROBAR CON CAMBIOS"}""", "");
        _bitbucket.Commits = [new PrCommit("c1full", "inicial", null)];
        var exporter = new ObsidianExporter(_config.VaultPath, _config.VaultFolder, (a, id) => $"{a} {id}", TimeZoneInfo.Utc);
        _service = new ReviewService(_config, _store, _bitbucket,
            name => AgentFactory.Create(name, _config, _runner), exporter, _ui, clock);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task First_review_starts_a_session_and_records_everything()
    {
        _ui.Note = "Ojo con la migración de datos.";

        var outcome = await _service.ReviewAsync(Ref, new ReviewOptions("claude"));

        var launch = _runner.Interactive.Single();
        Assert.Equal("claude", launch.Command);
        Assert.Equal(_dir.Path, launch.Cwd);
        Assert.Equal("--session-id", launch.Args[0]);
        Assert.Contains("https://bitbucket.org/acme/api/pull-requests/42", launch.Args[2]);
        Assert.Contains("feature/qr", launch.Args[2]);

        Assert.Equal(launch.Args[1], outcome.Session.AgentSessionId);
        Assert.Equal(RoundKinds.New, outcome.Round.Kind);
        Assert.Equal("APROBAR CON CAMBIOS", outcome.Pr.Verdict);
        Assert.Equal("c1full", outcome.Pr.LastReviewedCommit);
        Assert.Equal([MemoKinds.AgentSummary, MemoKinds.UserNote], _store.ListMemos(outcome.Pr.Id).Select(m => m.Kind));

        var note = File.ReadAllText(outcome.NotePath!);
        Assert.Contains("Ojo con la migración de datos.", note);
        Assert.Contains("- [ ] test", note);
        Assert.True(File.Exists(Path.Combine(_config.VaultPath!, "PR Reviews", "Index.md")));
    }

    [Fact]
    public async Task Second_review_resumes_the_same_session_and_asks_about_new_commits()
    {
        var first = await _service.ReviewAsync(Ref, new ReviewOptions("claude", AskNote: false));
        _bitbucket.Commits = [new PrCommit("c3full", "arregla timeout", null), new PrCommit("c2full", "agrega test", null), .. _bitbucket.Commits];

        var second = await _service.ReviewAsync(Ref, new ReviewOptions("claude", AskNote: false));

        Assert.Equal(first.Session.Id, second.Session.Id);
        var resume = _runner.Interactive[1].Args;
        Assert.Equal(["--resume", first.Session.AgentSessionId], resume[..2]);
        Assert.Contains("2 commit(s) nuevo(s)", resume[2]);
        Assert.Contains("c3full arregla timeout", resume[2]);
        Assert.Equal(RoundKinds.Resume, second.Round.Kind);
        Assert.Equal("c3full", second.Pr.LastReviewedCommit);
        Assert.Equal(2, _store.ListRounds(second.Pr.Id).Count);
        Assert.Contains("sesión retomada", File.ReadAllText(second.NotePath!));
    }

    [Fact]
    public async Task Resume_without_new_commits_sends_no_prompt()
    {
        await _service.ReviewAsync(Ref, new ReviewOptions("claude", Summary: false, AskNote: false));
        await _service.ReviewAsync(Ref, new ReviewOptions("claude", Summary: false, AskNote: false));

        Assert.Equal(2, _runner.Interactive[1].Args.Count);
        Assert.Empty(_runner.Captured);
    }

    [Fact]
    public async Task New_flag_and_other_agents_get_their_own_sessions()
    {
        var claude = await _service.ReviewAsync(Ref, new ReviewOptions("claude", Summary: false, AskNote: false));
        var fresh = await _service.ReviewAsync(Ref, new ReviewOptions("claude", ForceNewSession: true, Summary: false, AskNote: false));

        Assert.NotEqual(claude.Session.AgentSessionId, fresh.Session.AgentSessionId);
        Assert.Equal(fresh.Session.Id, _store.GetDefaultSession(fresh.Pr.Id, "claude")!.Id);
    }

    [Fact]
    public async Task Undetected_session_id_is_asked_to_the_user()
    {
        _ui.SessionId = "ses_manual";
        var outcome = await _service.ReviewAsync(Ref, new ReviewOptions("opencode", Summary: false, AskNote: false));
        Assert.Equal("ses_manual", outcome.Session.AgentSessionId);

        _ui.SessionId = null;
        await Assert.ThrowsAsync<AgentException>(
            () => _service.ReviewAsync(Ref, new ReviewOptions("opencode", ForceNewSession: true, Summary: false, AskNote: false)));
    }

    [Fact]
    public async Task Summary_failure_still_records_the_round()
    {
        _runner.OnCaptured = _ => new ProcessResult(1, "", "rate limit");

        var outcome = await _service.ReviewAsync(Ref, new ReviewOptions("claude", AskNote: false));

        Assert.Single(_store.ListRounds(outcome.Pr.Id));
        Assert.Empty(_store.ListMemos(outcome.Pr.Id));
        Assert.Contains(_ui.Messages, m => m.Contains("rate limit"));
    }

    [Fact]
    public async Task Works_offline_for_known_pull_requests()
    {
        await _service.ReviewAsync(Ref, new ReviewOptions("claude", Summary: false, AskNote: false));
        _bitbucket.Offline = true;

        var outcome = await _service.ReviewAsync(Ref, new ReviewOptions("claude", Summary: false, AskNote: false));

        Assert.Equal(RoundKinds.Resume, outcome.Round.Kind);
        Assert.Contains(_ui.Messages, m => m.StartsWith("warn: No se pudo consultar Bitbucket"));
    }

    [Fact]
    public async Task Unknown_pull_request_offline_fails()
    {
        _bitbucket.Offline = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => _service.ReviewAsync(Ref, new ReviewOptions("claude")));
    }

    [Fact]
    public void Prompt_placeholders_are_replaced_once()
    {
        var pr = new PullRequest { Workspace = "a", Repo = "b", Number = 1, Title = "T {url}", Description = "" };
        Assert.Equal("a/b#1 T {url} (sin descripción) {unknown}", Prompts.Render("{key} {title} {description} {unknown}", pr));
    }
}
