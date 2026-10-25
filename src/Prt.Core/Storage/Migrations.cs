using Microsoft.Data.Sqlite;

namespace Prt.Core.Storage;

/// <summary>Schema migrations, tracked with <c>PRAGMA user_version</c>. Append only.</summary>
internal static class Migrations
{
    private static readonly string[] Steps =
    [
        """
        CREATE TABLE pull_requests (
            id                   INTEGER PRIMARY KEY,
            workspace            TEXT NOT NULL,
            repo                 TEXT NOT NULL,
            number               INTEGER NOT NULL,
            title                TEXT NOT NULL DEFAULT '',
            author               TEXT NOT NULL DEFAULT '',
            source_branch        TEXT NOT NULL DEFAULT '',
            destination_branch   TEXT NOT NULL DEFAULT '',
            state                TEXT NOT NULL DEFAULT '',
            url                  TEXT NOT NULL DEFAULT '',
            description          TEXT NOT NULL DEFAULT '',
            last_reviewed_commit TEXT,
            verdict              TEXT,
            created_at           TEXT NOT NULL,
            updated_at           TEXT NOT NULL,
            UNIQUE (workspace, repo, number)
        );

        CREATE TABLE sessions (
            id               INTEGER PRIMARY KEY,
            pr_id            INTEGER NOT NULL REFERENCES pull_requests (id) ON DELETE CASCADE,
            agent            TEXT NOT NULL,
            agent_session_id TEXT NOT NULL,
            cwd              TEXT NOT NULL DEFAULT '',
            label            TEXT,
            is_default       INTEGER NOT NULL DEFAULT 0,
            started_at       TEXT NOT NULL,
            last_used_at     TEXT NOT NULL,
            UNIQUE (agent, agent_session_id)
        );
        CREATE INDEX ix_sessions_pr ON sessions (pr_id);

        CREATE TABLE rounds (
            id              INTEGER PRIMARY KEY,
            session_id      INTEGER NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
            kind            TEXT NOT NULL,
            started_at      TEXT NOT NULL,
            ended_at        TEXT,
            commit_at_start TEXT,
            prompt          TEXT
        );
        CREATE INDEX ix_rounds_session ON rounds (session_id);

        CREATE TABLE memos (
            id         INTEGER PRIMARY KEY,
            pr_id      INTEGER NOT NULL REFERENCES pull_requests (id) ON DELETE CASCADE,
            round_id   INTEGER REFERENCES rounds (id) ON DELETE SET NULL,
            kind       TEXT NOT NULL,
            body       TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE INDEX ix_memos_pr ON memos (pr_id);

        CREATE TABLE tags (
            pr_id INTEGER NOT NULL REFERENCES pull_requests (id) ON DELETE CASCADE,
            tag   TEXT NOT NULL,
            PRIMARY KEY (pr_id, tag)
        );
        """,
        """
        ALTER TABLE sessions ADD COLUMN reviewer TEXT;

        CREATE TABLE review_runs (
            id          INTEGER PRIMARY KEY,
            pr_id       INTEGER NOT NULL REFERENCES pull_requests (id) ON DELETE CASCADE,
            started_at  TEXT NOT NULL,
            ended_at    TEXT,
            head_commit TEXT,
            verdict     TEXT
        );
        CREATE INDEX ix_review_runs_pr ON review_runs (pr_id);

        CREATE TABLE reviewer_results (
            id          INTEGER PRIMARY KEY,
            run_id      INTEGER NOT NULL REFERENCES review_runs (id) ON DELETE CASCADE,
            reviewer    TEXT NOT NULL,
            agent       TEXT NOT NULL,
            session_id  INTEGER REFERENCES sessions (id) ON DELETE SET NULL,
            status      TEXT NOT NULL,
            report_json TEXT,
            raw_output  TEXT,
            error       TEXT,
            started_at  TEXT NOT NULL,
            ended_at    TEXT NOT NULL
        );
        CREATE INDEX ix_reviewer_results_run ON reviewer_results (run_id);
        """,
        """
        ALTER TABLE review_runs ADD COLUMN base_branch TEXT;
        ALTER TABLE review_runs ADD COLUMN base_commit TEXT;
        """,
    ];

    public static int LatestVersion => Steps.Length;

    public static void Apply(SqliteConnection connection)
    {
        var version = CurrentVersion(connection);
        if (version > Steps.Length)
            throw new InvalidOperationException(
                $"La base de datos tiene versión {version}, más nueva que esta versión de prt ({Steps.Length}). Actualizá prt.");

        for (var i = version; i < Steps.Length; i++)
        {
            using var tx = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = Steps[i] + $"\nPRAGMA user_version = {i + 1};";
                command.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public static int CurrentVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
