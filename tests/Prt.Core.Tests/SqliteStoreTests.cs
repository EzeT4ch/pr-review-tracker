using Prt.Core.Domain;
using Prt.Core.Storage;

namespace Prt.Core.Tests;

public class SqliteStoreTests
{
    private static SqliteStore NewStore() => new(":memory:", new FakeClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)));

    private static PullRequest Pr(int number = 1, string title = "t") =>
        new() { Workspace = "acme", Repo = "api", Number = number, Title = title, State = "OPEN" };

    [Fact]
    public void Upsert_updates_metadata_and_keeps_review_state()
    {
        using var store = NewStore();
        var pr = store.UpsertPullRequest(Pr(title: "viejo"));
        store.SetReviewState(pr.Id, "abc", "APROBAR");

        var updated = store.UpsertPullRequest(Pr(title: "nuevo"));

        Assert.Equal(pr.Id, updated.Id);
        Assert.Equal("nuevo", updated.Title);
        Assert.Equal("abc", updated.LastReviewedCommit);
        Assert.Equal("APROBAR", updated.Verdict);
    }

    [Fact]
    public void New_session_becomes_default_for_its_agent()
    {
        using var store = NewStore();
        var pr = store.UpsertPullRequest(Pr());
        store.AddSession(pr.Id, "claude", "s1", "/repo");
        store.AddSession(pr.Id, "codex", "c1", "/repo");
        store.AddSession(pr.Id, "claude", "s2", "/repo");

        Assert.Equal("s2", store.GetDefaultSession(pr.Id, "claude")!.AgentSessionId);
        Assert.Equal("c1", store.GetDefaultSession(pr.Id, "codex")!.AgentSessionId);
        Assert.Single(store.ListSessions(pr.Id), s => s.Agent == "claude" && s.IsDefault);
        Assert.Equal(3, store.ListSessions(pr.Id).Count);
    }

    [Fact]
    public void Relinking_a_session_moves_it_without_duplicating()
    {
        using var store = NewStore();
        var a = store.UpsertPullRequest(Pr(1));
        var b = store.UpsertPullRequest(Pr(2));
        store.AddSession(a.Id, "claude", "s1", "/repo");
        store.AddSession(b.Id, "claude", "s1", "/repo");

        Assert.Empty(store.ListSessions(a.Id));
        Assert.Single(store.ListSessions(b.Id));
    }

    [Fact]
    public void History_contains_rounds_memos_and_tags_in_order()
    {
        using var store = NewStore();
        var pr = store.UpsertPullRequest(Pr());
        var session = store.AddSession(pr.Id, "claude", "s1", "/repo");
        var start = DateTimeOffset.UtcNow;
        var round = store.AddRound(session.Id, RoundKinds.New, start, start.AddMinutes(5), "abc", "prompt");
        store.AddMemo(pr.Id, round.Id, MemoKinds.AgentSummary, "  resumen  ");
        store.AddMemo(pr.Id, null, MemoKinds.Decision, "decisión");
        store.AddTags(pr.Id, ["#Arquitectura", "pagos qr", "pagos qr"]);

        var history = store.GetHistory(pr.Id);

        Assert.Single(history.Rounds);
        Assert.Equal(["resumen", "decisión"], history.Memos.Select(m => m.Body));
        Assert.Equal(["arquitectura", "pagos-qr"], history.Tags);
        Assert.Equal(start.AddMinutes(5).ToUnixTimeMilliseconds(), history.Rounds[0].EndedAt!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Lists_by_last_activity_with_filters()
    {
        using var store = NewStore();
        var older = store.UpsertPullRequest(Pr(1));
        var newer = store.UpsertPullRequest(Pr(2));
        store.UpsertPullRequest(new PullRequest { Workspace = "acme", Repo = "web", Number = 3, State = "MERGED" });
        store.AddMemo(older.Id, null, MemoKinds.UserNote, "actividad reciente");

        Assert.Equal([1, 3, 2], store.ListPullRequests().Select(p => p.Number));
        Assert.Equal([1, 2], store.ListPullRequests(state: "open").Select(p => p.Number));
        Assert.Equal([3], store.ListPullRequests(repoKey: "ACME/web").Select(p => p.Number));
        Assert.True(store.LastActivity(older.Id) > store.LastActivity(newer.Id));
    }

    [Fact]
    public void Rejects_unknown_memo_kind()
    {
        using var store = NewStore();
        var pr = store.UpsertPullRequest(Pr());
        Assert.Throws<ArgumentException>(() => store.AddMemo(pr.Id, null, "otro", "x"));
    }

    [Fact]
    public void Reopening_a_database_file_keeps_data()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sub", "prt.db");
        using (var store = new SqliteStore(path))
            store.UpsertPullRequest(Pr());

        using var reopened = new SqliteStore(path);
        Assert.NotNull(reopened.FindPullRequest(new PrRef("acme", "api", 1)));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
