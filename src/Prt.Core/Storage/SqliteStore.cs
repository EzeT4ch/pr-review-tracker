using System.Globalization;
using Microsoft.Data.Sqlite;
using Prt.Core.Domain;

namespace Prt.Core.Storage;

/// <summary>SQLite store; the source of truth for PRs, sessions, rounds and memos.</summary>
public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;
    private SqliteTransaction? _transaction;

    public SqliteStore(string databasePath, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        if (databasePath != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        _connection.Open();
        Execute("PRAGMA foreign_keys = ON;");
        // The CLI and the web panel may write at the same time: WAL lets readers proceed during a write,
        // and the timeout makes a second writer wait instead of failing.
        Execute("PRAGMA busy_timeout = 5000;");
        if (databasePath != ":memory:")
            Execute("PRAGMA journal_mode = WAL;");
        Migrations.Apply(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private DateTimeOffset Now => _clock.GetUtcNow();

    // ---- Pull requests -------------------------------------------------------------------

    /// <summary>Inserts or updates the PR metadata (review state is left untouched) and returns it.</summary>
    public PullRequest UpsertPullRequest(PullRequest pr)
    {
        var now = Now;
        Execute(
            """
            INSERT INTO pull_requests (workspace, repo, number, title, author, source_branch, destination_branch,
                                       state, url, description, created_at, updated_at)
            VALUES ($ws, $repo, $n, $title, $author, $src, $dst, $state, $url, $desc, $now, $now)
            ON CONFLICT (workspace, repo, number) DO UPDATE SET
                title = excluded.title, author = excluded.author, source_branch = excluded.source_branch,
                destination_branch = excluded.destination_branch, state = excluded.state, url = excluded.url,
                description = excluded.description, updated_at = excluded.updated_at;
            """,
            ("$ws", pr.Workspace), ("$repo", pr.Repo), ("$n", pr.Number), ("$title", pr.Title),
            ("$author", pr.Author), ("$src", pr.SourceBranch), ("$dst", pr.DestinationBranch),
            ("$state", pr.State), ("$url", pr.Url), ("$desc", pr.Description), ("$now", Format(now)));

        return FindPullRequest(pr.Ref)!;
    }

    public PullRequest? FindPullRequest(PrRef prRef) =>
        Query(PrSelect + " WHERE workspace = $ws AND repo = $repo AND number = $n", ReadPr,
            ("$ws", prRef.Workspace), ("$repo", prRef.Repo), ("$n", prRef.Number)).FirstOrDefault();

    public PullRequest? GetPullRequest(long id) =>
        Query(PrSelect + " WHERE id = $id", ReadPr, ("$id", id)).FirstOrDefault();

    /// <summary>PRs ordered by last activity (latest round or memo, falling back to metadata update).</summary>
    public IReadOnlyList<PullRequest> ListPullRequests(string? state = null, string? repoKey = null)
    {
        var sql = PrSelect + " WHERE 1 = 1";
        var parameters = new List<(string, object?)>();
        if (!string.IsNullOrEmpty(state))
        {
            sql += " AND lower(state) = lower($state)";
            parameters.Add(("$state", state));
        }
        if (!string.IsNullOrEmpty(repoKey))
        {
            sql += " AND lower(workspace || '/' || repo) = lower($repo)";
            parameters.Add(("$repo", repoKey));
        }
        sql += " ORDER BY " + LastActivitySql + " DESC";
        return Query(sql, ReadPr, [.. parameters]);
    }

    public DateTimeOffset LastActivity(long prId) =>
        Parse(Scalar<string>("SELECT " + LastActivitySql + " FROM pull_requests p WHERE id = $id", ("$id", prId))!);

    public void SetReviewState(long prId, string? lastReviewedCommit, string? verdict)
    {
        Execute(
            """
            UPDATE pull_requests
            SET last_reviewed_commit = COALESCE($commit, last_reviewed_commit),
                verdict = COALESCE($verdict, verdict)
            WHERE id = $id
            """,
            ("$commit", lastReviewedCommit), ("$verdict", verdict), ("$id", prId));
    }

    // ---- Sessions ------------------------------------------------------------------------

    /// <summary>
    /// Adds a session and makes it the default one for its agent on that PR. <paramref name="reviewer"/>
    /// is the panel reviewer it belongs to; null for the interactive review session.
    /// </summary>
    public ReviewSession AddSession(long prId, string agent, string agentSessionId, string cwd, string? label = null, string? reviewer = null)
    {
        var now = Format(Now);
        InTransaction(() =>
        {
            Execute("UPDATE sessions SET is_default = 0 WHERE pr_id = $pr AND agent = $agent AND reviewer IS $reviewer",
                ("$pr", prId), ("$agent", agent), ("$reviewer", reviewer));
            Execute(
                """
                INSERT INTO sessions (pr_id, agent, agent_session_id, cwd, label, reviewer, is_default, started_at, last_used_at)
                VALUES ($pr, $agent, $sid, $cwd, $label, $reviewer, 1, $now, $now)
                ON CONFLICT (agent, agent_session_id) DO UPDATE SET
                    pr_id = excluded.pr_id, cwd = excluded.cwd, is_default = 1, reviewer = excluded.reviewer,
                    label = COALESCE(excluded.label, sessions.label), last_used_at = excluded.last_used_at
                """,
                ("$pr", prId), ("$agent", agent), ("$sid", agentSessionId), ("$cwd", cwd), ("$label", label),
                ("$reviewer", reviewer), ("$now", now));
        });

        return Query(SessionSelect + " WHERE agent = $agent AND agent_session_id = $sid", ReadSession,
            ("$agent", agent), ("$sid", agentSessionId)).Single();
    }

    public ReviewSession? GetDefaultSession(long prId, string agent, string? reviewer = null) =>
        Query(SessionSelect + " WHERE pr_id = $pr AND agent = $agent AND reviewer IS $reviewer ORDER BY is_default DESC, last_used_at DESC LIMIT 1",
            ReadSession, ("$pr", prId), ("$agent", agent), ("$reviewer", reviewer)).FirstOrDefault();

    /// <summary>The session used most recently on the PR by the interactive review (or by a reviewer), any agent.</summary>
    public ReviewSession? GetLatestSession(long prId, string? reviewer = null) =>
        Query(SessionSelect + " WHERE pr_id = $pr AND reviewer IS $reviewer ORDER BY last_used_at DESC LIMIT 1", ReadSession,
            ("$pr", prId), ("$reviewer", reviewer)).FirstOrDefault();

    public ReviewSession? GetSession(long id) =>
        Query(SessionSelect + " WHERE id = $id", ReadSession, ("$id", id)).FirstOrDefault();

    public IReadOnlyList<ReviewSession> ListSessions(long prId) =>
        Query(SessionSelect + " WHERE pr_id = $pr ORDER BY started_at", ReadSession, ("$pr", prId));

    public void TouchSession(long sessionId) =>
        Execute("UPDATE sessions SET last_used_at = $now WHERE id = $id", ("$now", Format(Now)), ("$id", sessionId));

    // ---- Rounds --------------------------------------------------------------------------

    public Round AddRound(long sessionId, string kind, DateTimeOffset startedAt, DateTimeOffset? endedAt,
        string? commitAtStart, string? prompt)
    {
        var id = Scalar<long>(
            """
            INSERT INTO rounds (session_id, kind, started_at, ended_at, commit_at_start, prompt)
            VALUES ($s, $kind, $start, $end, $commit, $prompt);
            SELECT last_insert_rowid();
            """,
            ("$s", sessionId), ("$kind", kind), ("$start", Format(startedAt)),
            ("$end", endedAt is { } e ? Format(e) : null), ("$commit", commitAtStart), ("$prompt", prompt));
        return Query(RoundSelect + " WHERE r.id = $id", ReadRound, ("$id", id)).Single();
    }

    public IReadOnlyList<Round> ListRounds(long prId) =>
        Query(RoundSelect + " JOIN sessions s ON s.id = r.session_id WHERE s.pr_id = $pr ORDER BY r.started_at",
            ReadRound, ("$pr", prId));

    // ---- Memos ---------------------------------------------------------------------------

    public Memo AddMemo(long prId, long? roundId, string kind, string body)
    {
        if (!MemoKinds.All.Contains(kind))
            throw new ArgumentException($"Tipo de memo desconocido '{kind}'. Opciones: {string.Join(", ", MemoKinds.All)}.");

        var id = Scalar<long>(
            """
            INSERT INTO memos (pr_id, round_id, kind, body, created_at) VALUES ($pr, $round, $kind, $body, $now);
            SELECT last_insert_rowid();
            """,
            ("$pr", prId), ("$round", roundId), ("$kind", kind), ("$body", body.Trim()), ("$now", Format(Now)));
        return Query(MemoSelect + " WHERE id = $id", ReadMemo, ("$id", id)).Single();
    }

    public IReadOnlyList<Memo> ListMemos(long prId) =>
        Query(MemoSelect + " WHERE pr_id = $pr ORDER BY created_at, id", ReadMemo, ("$pr", prId));

    // ---- Panel runs ----------------------------------------------------------------------

    public ReviewRun StartRun(long prId, string? headCommit, DateTimeOffset? startedAt = null, string? baseBranch = null, string? baseCommit = null)
    {
        var id = Scalar<long>(
            """
            INSERT INTO review_runs (pr_id, started_at, head_commit, base_branch, base_commit) VALUES ($pr, $start, $head, $bb, $bc);
            SELECT last_insert_rowid();
            """,
            ("$pr", prId), ("$start", Format(startedAt ?? Now)), ("$head", headCommit), ("$bb", baseBranch), ("$bc", baseCommit));
        return Query(RunSelect + " WHERE id = $id", ReadRun, ("$id", id)).Single();
    }

    public void FinishRun(long runId, string? verdict) =>
        Execute("UPDATE review_runs SET ended_at = $end, verdict = $verdict WHERE id = $id",
            ("$end", Format(Now)), ("$verdict", verdict), ("$id", runId));

    public ReviewerResult AddResult(ReviewerResult result)
    {
        result.Id = Scalar<long>(
            """
            INSERT INTO reviewer_results (run_id, reviewer, agent, session_id, status, report_json, raw_output, error, started_at, ended_at)
            VALUES ($run, $reviewer, $agent, $session, $status, $report, $raw, $error, $start, $end);
            SELECT last_insert_rowid();
            """,
            ("$run", result.RunId), ("$reviewer", result.Reviewer), ("$agent", result.Agent), ("$session", result.SessionId),
            ("$status", result.Status), ("$report", result.ReportJson), ("$raw", result.RawOutput), ("$error", result.Error),
            ("$start", Format(result.StartedAt)), ("$end", Format(result.EndedAt)));
        return result;
    }

    public IReadOnlyList<RunHistory> ListRuns(long prId)
    {
        var runs = Query(RunSelect + " WHERE pr_id = $pr ORDER BY started_at, id", ReadRun, ("$pr", prId));
        var results = Query(
            ResultSelect + " WHERE run_id IN (SELECT id FROM review_runs WHERE pr_id = $pr) ORDER BY id",
            ReadResult, ("$pr", prId)).ToLookup(r => r.RunId);
        return runs.Select(r => new RunHistory(r, results[r.Id].ToList())).ToList();
    }

    /// <summary>The last valid report of a reviewer on a PR, to carry its findings into the next run.</summary>
    public ReviewerResult? LatestOkResult(long prId, string reviewer) =>
        Query(ResultSelect + """
             WHERE reviewer = $reviewer AND status = 'ok'
               AND run_id IN (SELECT id FROM review_runs WHERE pr_id = $pr)
             ORDER BY id DESC LIMIT 1
            """, ReadResult, ("$pr", prId), ("$reviewer", reviewer)).FirstOrDefault();

    // ---- Tags ----------------------------------------------------------------------------

    public void AddTags(long prId, IEnumerable<string> tags)
    {
        foreach (var tag in tags.Select(NormalizeTag).Where(t => t.Length > 0))
            Execute("INSERT OR IGNORE INTO tags (pr_id, tag) VALUES ($pr, $tag)", ("$pr", prId), ("$tag", tag));
    }

    public void RemoveTags(long prId, IEnumerable<string> tags)
    {
        foreach (var tag in tags.Select(NormalizeTag))
            Execute("DELETE FROM tags WHERE pr_id = $pr AND tag = $tag", ("$pr", prId), ("$tag", tag));
    }

    public IReadOnlyList<string> ListTags(long prId) =>
        Query("SELECT tag FROM tags WHERE pr_id = $pr ORDER BY tag", r => r.GetString(0), ("$pr", prId));

    private static string NormalizeTag(string tag) =>
        string.Join('-', tag.Trim().TrimStart('#').Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public PrHistory GetHistory(long prId)
    {
        var pr = GetPullRequest(prId) ?? throw new InvalidOperationException($"PR {prId} no existe.");
        return new PrHistory(pr, ListSessions(prId), ListRounds(prId), ListMemos(prId), ListTags(prId), ListRuns(prId));
    }

    // ---- Mapping -------------------------------------------------------------------------

    private const string LastActivitySql =
        """
        COALESCE(
            (SELECT MAX(x) FROM (
                SELECT MAX(COALESCE(r.ended_at, r.started_at)) AS x FROM rounds r JOIN sessions s ON s.id = r.session_id WHERE s.pr_id = p.id
                UNION ALL SELECT MAX(m.created_at) FROM memos m WHERE m.pr_id = p.id
                UNION ALL SELECT MAX(COALESCE(v.ended_at, v.started_at)) FROM review_runs v WHERE v.pr_id = p.id)),
            p.updated_at)
        """;

    private const string PrSelect =
        """
        SELECT p.id, p.workspace, p.repo, p.number, p.title, p.author, p.source_branch, p.destination_branch, p.state,
               p.url, p.description, p.last_reviewed_commit, p.verdict, p.created_at, p.updated_at
        FROM pull_requests p
        """;

    private static PullRequest ReadPr(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Workspace = r.GetString(1),
        Repo = r.GetString(2),
        Number = r.GetInt32(3),
        Title = r.GetString(4),
        Author = r.GetString(5),
        SourceBranch = r.GetString(6),
        DestinationBranch = r.GetString(7),
        State = r.GetString(8),
        Url = r.GetString(9),
        Description = r.GetString(10),
        LastReviewedCommit = r.IsDBNull(11) ? null : r.GetString(11),
        Verdict = r.IsDBNull(12) ? null : r.GetString(12),
        CreatedAt = Parse(r.GetString(13)),
        UpdatedAt = Parse(r.GetString(14)),
    };

    private const string SessionSelect =
        "SELECT id, pr_id, agent, agent_session_id, cwd, label, is_default, started_at, last_used_at, reviewer FROM sessions";

    private static ReviewSession ReadSession(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        PrId = r.GetInt64(1),
        Agent = r.GetString(2),
        AgentSessionId = r.GetString(3),
        Cwd = r.GetString(4),
        Label = r.IsDBNull(5) ? null : r.GetString(5),
        IsDefault = r.GetInt64(6) != 0,
        StartedAt = Parse(r.GetString(7)),
        LastUsedAt = Parse(r.GetString(8)),
        Reviewer = r.IsDBNull(9) ? null : r.GetString(9),
    };

    private const string RunSelect = "SELECT id, pr_id, started_at, ended_at, head_commit, verdict, base_branch, base_commit FROM review_runs";

    private static ReviewRun ReadRun(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        PrId = r.GetInt64(1),
        StartedAt = Parse(r.GetString(2)),
        EndedAt = r.IsDBNull(3) ? null : Parse(r.GetString(3)),
        HeadCommit = r.IsDBNull(4) ? null : r.GetString(4),
        Verdict = r.IsDBNull(5) ? null : r.GetString(5),
        BaseBranch = r.IsDBNull(6) ? null : r.GetString(6),
        BaseCommit = r.IsDBNull(7) ? null : r.GetString(7),
    };

    private const string ResultSelect =
        "SELECT id, run_id, reviewer, agent, session_id, status, report_json, raw_output, error, started_at, ended_at FROM reviewer_results";

    private static ReviewerResult ReadResult(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        RunId = r.GetInt64(1),
        Reviewer = r.GetString(2),
        Agent = r.GetString(3),
        SessionId = r.IsDBNull(4) ? null : r.GetInt64(4),
        Status = r.GetString(5),
        ReportJson = r.IsDBNull(6) ? null : r.GetString(6),
        RawOutput = r.IsDBNull(7) ? null : r.GetString(7),
        Error = r.IsDBNull(8) ? null : r.GetString(8),
        StartedAt = Parse(r.GetString(9)),
        EndedAt = Parse(r.GetString(10)),
    };

    private const string RoundSelect =
        "SELECT r.id, r.session_id, r.kind, r.started_at, r.ended_at, r.commit_at_start, r.prompt FROM rounds r";

    private static Round ReadRound(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        SessionId = r.GetInt64(1),
        Kind = r.GetString(2),
        StartedAt = Parse(r.GetString(3)),
        EndedAt = r.IsDBNull(4) ? null : Parse(r.GetString(4)),
        CommitAtStart = r.IsDBNull(5) ? null : r.GetString(5),
        Prompt = r.IsDBNull(6) ? null : r.GetString(6),
    };

    private const string MemoSelect = "SELECT id, pr_id, round_id, kind, body, created_at FROM memos";

    private static Memo ReadMemo(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        PrId = r.GetInt64(1),
        RoundId = r.IsDBNull(2) ? null : r.GetInt64(2),
        Kind = r.GetString(3),
        Body = r.GetString(4),
        CreatedAt = Parse(r.GetString(5)),
    };

    // ISO 8601 UTC with fixed width, so text ordering in SQL matches chronological ordering.
    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    // ---- ADO helpers ---------------------------------------------------------------------

    private void InTransaction(Action body)
    {
        using var tx = _connection.BeginTransaction();
        _transaction = tx;
        try
        {
            body();
            tx.Commit();
        }
        finally
        {
            _transaction = null;
        }
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void Execute(string sql, params (string, object?)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private T? Scalar<T>(string sql, params (string, object?)[] parameters)
    {
        using var command = Command(sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(map(reader));
        return results;
    }
}
