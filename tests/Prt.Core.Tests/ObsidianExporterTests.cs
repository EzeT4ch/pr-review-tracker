using Prt.Core.Domain;
using Prt.Core.Export;

namespace Prt.Core.Tests;

public class ObsidianExporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 10, 14, 0, 0, TimeSpan.Zero);

    private static ObsidianExporter Exporter(string vault) =>
        new(vault, "PR Reviews", (agent, id) => $"{agent} --resume {id}", TimeZoneInfo.Utc);

    private static PrHistory History(string title = "Pagos: QR / v2", string? verdict = "APROBAR")
    {
        var pr = new PullRequest
        {
            Id = 1, Workspace = "acme", Repo = "api", Number = 42, Title = title, Author = "Ana",
            SourceBranch = "feature/qr", DestinationBranch = "main", State = "OPEN",
            Url = "https://bitbucket.org/acme/api/pull-requests/42", Description = "## Contexto\nCobro \"QR\"",
            Verdict = verdict, LastReviewedCommit = "0123456789abcdef", CreatedAt = T0, UpdatedAt = T0,
        };
        var session = new ReviewSession
        {
            Id = 7, PrId = 1, Agent = "claude", AgentSessionId = "uuid-1", Cwd = "/repo", IsDefault = true,
            StartedAt = T0, LastUsedAt = T0.AddHours(1),
        };
        var round = new Round { Id = 3, SessionId = 7, Kind = RoundKinds.New, StartedAt = T0, EndedAt = T0.AddMinutes(30), CommitAtStart = "0123456789abcdef", Prompt = "Revisá" };
        Memo[] memos =
        [
            new() { Id = 1, PrId = 1, RoundId = 3, Kind = MemoKinds.AgentSummary, CreatedAt = T0.AddMinutes(31),
                Body = "## Hallazgos\n- falta test\n\n## Pendientes\n- [ ] test de timeout\n\nVeredicto: APROBAR" },
            new() { Id = 2, PrId = 1, RoundId = null, Kind = MemoKinds.Decision, CreatedAt = T0.AddDays(1), Body = "Se acepta deuda técnica." },
        ];
        return new PrHistory(pr, [session], [round], memos, ["pagos"]);
    }

    [Fact]
    public void Writes_note_with_frontmatter_sessions_pending_and_timeline()
    {
        using var dir = new TempDir();
        var path = Exporter(dir.Path).ExportPr(History());

        Assert.Equal(Path.Combine(dir.Path, "PR Reviews", "acme", "api", "PR-42 - Pagos QR v2.md"), path);
        var text = File.ReadAllText(path);
        Assert.StartsWith("---\npr: \"acme/api#42\"\n", text);
        Assert.Contains("    resume: \"claude --resume uuid-1\"\n", text);
        Assert.Contains("tags:\n  - \"pr-review\"\n  - \"pagos\"\n", text);
        Assert.Contains("last_review: \"2026-03-11T14:00\"", text);
        Assert.Contains("| revisión | claude ⭐ | `uuid-1` |", text);
        Assert.Contains("_Sin análisis todavía.", text);
        Assert.Contains("## Pendientes\n\n_Según el último resumen (2026-03-10 14:31)._\n\n- [ ] test de timeout\n", text);
        Assert.Contains("### 2026-03-10 14:00 — claude · nueva sesión · `0123456789ab`", text);
        Assert.Contains("> [!abstract] Resumen del agente · 14:31\n> #### Hallazgos\n", text);
        Assert.Contains("### 2026-03-11 14:00 — Decisión", text);
        Assert.True(text.IndexOf("nueva sesión", StringComparison.Ordinal) < text.IndexOf("— Decisión", StringComparison.Ordinal));
        Assert.Contains(ObsidianExporter.EndMarker + "\n\n## Notas libres\n", text);
    }

    [Fact]
    public void Regeneration_preserves_user_content_and_renames_on_title_change()
    {
        using var dir = new TempDir();
        var exporter = Exporter(dir.Path);
        var first = exporter.ExportPr(History(title: "Viejo"));
        var edited = File.ReadAllText(first)
            .Replace(ObsidianExporter.BeginMarker, "Arriba mío\n" + ObsidianExporter.BeginMarker)
            + "Mi análisis de negocio.\n";
        File.WriteAllText(first, edited);

        var second = exporter.ExportPr(History(title: "Nuevo", verdict: "BLOQUEADO"));

        Assert.False(File.Exists(first));
        Assert.EndsWith("PR-42 - Nuevo.md", second);
        var text = File.ReadAllText(second);
        Assert.Contains("Arriba mío\n" + ObsidianExporter.BeginMarker, text);
        Assert.EndsWith("## Notas libres\n\nMi análisis de negocio.\n", text);
        Assert.Contains("verdict: \"BLOQUEADO\"", text);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(second)!));
    }

    [Fact]
    public void Keeps_content_when_markers_were_removed()
    {
        using var dir = new TempDir();
        var exporter = Exporter(dir.Path);
        var path = exporter.ExportPr(History());
        File.WriteAllText(path, "---\npr: x\n---\nTodo reescrito a mano.\n");

        exporter.ExportPr(History());

        var text = File.ReadAllText(path);
        Assert.Contains(ObsidianExporter.BeginMarker, text);
        Assert.EndsWith(ObsidianExporter.EndMarker + "\n\nTodo reescrito a mano.\n", text);
    }

    [Fact]
    public void Memo_tables_survive_inside_the_callout_and_html_like_text_is_escaped()
    {
        using var dir = new TempDir();
        var history = History();
        Memo memo = new()
        {
            Id = 3, PrId = 1, RoundId = 3, Kind = MemoKinds.AgentSummary, CreatedAt = T0.AddDays(2),
            Body = "## Hallazgos\n\n| ID | Sev. | Hallazgo | Ubicación | Acción |\n|---|---|---|---|---|\n" +
                   "| B1 | 🔴 | Mock de IRepository<ProductCode> | `Tests.cs:36` | Usar `SetupFilter` |\n\n## Pendientes\n- [ ] revisar `List<int>` y List<int>",
        };
        var path = Exporter(dir.Path).ExportPr(history with { Memos = [.. history.Memos, memo] });

        var text = File.ReadAllText(path);
        Assert.Contains("> | ID | Sev. | Hallazgo | Ubicación | Acción |\n> |---|---|---|---|---|\n" +
                        "> | B1 | 🔴 | Mock de IRepository\\<ProductCode> | `Tests.cs:36` | Usar `SetupFilter` |\n", text);
        Assert.Contains("- [ ] revisar `List<int>` y List\\<int>\n", text);
    }

    [Fact]
    public void Index_links_notes_and_escapes_tables()
    {
        using var dir = new TempDir();
        var exporter = Exporter(dir.Path);
        var path = exporter.ExportIndex([History(title: "a | b")]);

        var text = File.ReadAllText(path);
        Assert.Contains("| [[PR Reviews/acme/api/PR-42 - a b\\|acme/api#42]] | a \\| b | Ana | APROBAR |  | 2026-03-11 14:00 | 1 |", text);
        Assert.Contains("```dataview", text);
    }

    [Theory]
    [InlineData("feat: a/b [wip] #1", "feat a b wip 1")]
    [InlineData("  ..hola..  ", "hola")]
    public void Sanitizes_file_names(string input, string expected) => Assert.Equal(expected, ObsidianExporter.SafeName(input));
}
