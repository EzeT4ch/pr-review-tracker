using System.Text.Json;
using System.Text.RegularExpressions;
using Prt.Core.Agents;
using Prt.Core.Bitbucket;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Export;
using Prt.Core.Panel;
using Prt.Core.Storage;

namespace Prt.Core.Tests;

public sealed class GroupPanelServiceTests : IDisposable
{
    private static readonly PrRef Api = new("acme", "api", 1);
    private static readonly PrRef Web = new("acme", "web", 2);

    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteStore _store;
    private readonly FakeRunner _runner = new();
    private readonly FakeBitbucket _bitbucket = new() { Commits = [new PrCommit("c1full", "inicial", null)] };
    private readonly FakeUi _ui = new();
    private readonly PrtConfig _config = new();
    private readonly ObsidianExporter _exporter;
    private readonly Dictionary<string, string> _answers = new();
    private readonly List<(string Reviewer, List<string> Args)> _calls = [];
    private PrGroup _group = null!;

    public GroupPanelServiceTests()
    {
        _store = new SqliteStore(":memory:", _clock);
        _config.VaultPath = Path.Combine(_dir.Path, "vault");
        _config.RepoPaths["acme/api"] = Directory.CreateDirectory(Path.Combine(_dir.Path, "api")).FullName;
        _config.RepoPaths["acme/web"] = Directory.CreateDirectory(Path.Combine(_dir.Path, "web")).FullName;
        _exporter = new ObsidianExporter(_config.VaultPath, _config.VaultFolder, (a, id) => $"{a} {id}", TimeZoneInfo.Utc);

        _runner.OnCaptured = args =>
        {
            var reviewer = Regex.Match(args[^1], "\"reviewer\": \"(\\w+)\"").Groups[1].Value;
            lock (_calls)
                _calls.Add((reviewer, args));
            var answer = _answers.GetValueOrDefault(reviewer) ?? Report(reviewer, "approve");
            return new ProcessResult(0, JsonSerializer.Serialize(new { type = "result", is_error = false, result = answer }), "");
        };
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private GroupPanelService Service() => new(
        _config, _store, _bitbucket, name => AgentFactory.Create(name, _config, _runner), _exporter, _ui,
        Path.Combine(_dir.Path, "runs"), _clock, (_, _) => ProfileLoader.Load([]), () => ProfileLoader.Load([]));

    private void CreateGroup(params PrRef[] refs)
    {
        _group = _store.CreateGroup("feature/qr", "Cobro con QR en la API y en la web.");
        _store.AddGroupMembers(_group.Id, refs.Select(r => _store.UpsertPullRequest(new PullRequest { Workspace = r.Workspace, Repo = r.Repo, Number = r.Number }).Id));
    }

    private static string Report(string reviewer, string verdict, params object[] findings) =>
        JsonSerializer.Serialize(new { reviewer, verdict, summary = "Resumen.", findings, questions = Array.Empty<string>() });

    private static object Finding(string id, string severity, string? pr) =>
        new { id, severity, title = $"Hallazgo {id}", file = "src/Pay.cs", line = 3, description = "d", recommendation = "r", pr };

    [Fact]
    public async Task Each_reviewer_sees_every_pr_and_each_pr_gets_its_share_of_the_findings()
    {
        CreateGroup(Api, Web);
        _answers["coordination"] = Report("coordination", "block",
            Finding("COORD-1", "blocker", "acme/api#1"), Finding("COORD-2", "major", null));
        _answers["security"] = Report("security", "approve_with_comments", Finding("SEC-1", "minor", "web#2"));

        var outcome = await Service().AnalyzeAsync(_group.Id, new PanelOptions());

        Assert.Equal(["architecture", "bugs", "performance", "security", "coordination"], outcome.Reviewers.Select(r => r.Reviewer.Name));
        Assert.Equal(Verdict.Block, outcome.Verdict);
        Assert.Equal(Verdict.Block, outcome.PrVerdicts[Api.Key]);
        Assert.Equal(Verdict.RequestChanges, outcome.PrVerdicts[Web.Key]);
        Assert.Equal("BLOQUEADO", _store.GetGroup(_group.Id)!.Verdict);

        // One session for the whole group: the prompt lists both PRs, and claude may read both repos.
        var (_, args) = _calls.Single(c => c.Reviewer == "coordination");
        var prompt = args[^1];
        Assert.Contains("en conjunto", prompt);
        Assert.Contains("acme/api#1", prompt);
        Assert.Contains("acme/web#2", prompt);
        Assert.Contains("\"pr\": \"acme/api#1 | acme/web#2 | null\"", prompt);
        Assert.Contains(_config.RepoPaths["acme/api"], args);
        Assert.Contains(_config.RepoPaths["acme/web"], args);
        Assert.Contains("Revisás que los cambios de los distintos repos", prompt);

        // Each PR gets a regular run, derived from the joint one, with its findings plus the cross-cutting ones.
        var apiPr = _store.FindPullRequest(Api)!;
        var apiRun = Assert.Single(_store.ListRuns(apiPr.Id));
        Assert.Equal(outcome.Run.Id, apiRun.Run.GroupRunId);
        Assert.Equal("BLOQUEADO", apiRun.Run.Verdict);
        Assert.Equal(["COORD-1", "COORD-2"], RunView.From(apiRun).OpenFindings.Select(x => x.Finding.Id).Order());
        var webRun = Assert.Single(_store.ListRuns(_store.FindPullRequest(Web)!.Id));
        Assert.Equal(["COORD-2", "SEC-1"], RunView.From(webRun).OpenFindings.Select(x => x.Finding.Id).Order());
        Assert.Equal("CAMBIOS REQUERIDOS", _store.FindPullRequest(Web)!.Verdict);
        Assert.True(File.Exists(UnifiedDiff.RunDiffPath(Path.Combine(_dir.Path, "runs"), Api, apiRun.Run.Id)));

        // Exported: a note for the group (findings by PR) and the PR notes link to it.
        var groupNote = File.ReadAllText(outcome.NotePath!);
        Assert.Contains("# Grupo · feature/qr", groupNote);
        Assert.Contains("#### acme/api#1", groupNote);
        Assert.Contains("#### Transversales", groupNote);
        var apiNote = File.ReadAllText(Directory.GetFiles(_exporter.NoteDirectory(apiPr), "PR-1*.md").Single());
        Assert.Contains("Parte del grupo", apiNote);
    }

    [Fact]
    public async Task Sessions_are_resumed_until_the_group_changes()
    {
        CreateGroup(Api, Web);
        await Service().AnalyzeAsync(_group.Id, new PanelOptions(Reviewers: ["coordination"]));
        await Service().AnalyzeAsync(_group.Id, new PanelOptions(Reviewers: ["coordination"]));

        Assert.Contains("--session-id", _calls[0].Args);
        Assert.Contains("--resume", _calls[1].Args);
        Assert.Contains("Nueva pasada de revisión conjunta", _calls[1].Args[^1]);

        var third = new PrRef("acme", "infra", 3);
        _store.AddGroupMembers(_group.Id, [_store.UpsertPullRequest(new PullRequest { Workspace = "acme", Repo = "infra", Number = 3 }).Id]);
        await Service().AnalyzeAsync(_group.Id, new PanelOptions(Reviewers: ["coordination"]));

        Assert.Contains("--session-id", _calls[2].Args);
        Assert.Contains(third.Key, _calls[2].Args[^1]);
        Assert.Contains(_ui.Messages, m => m.Contains("el grupo cambió"));
    }

    [Fact]
    public async Task A_group_needs_two_prs()
    {
        CreateGroup(Api);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Service().AnalyzeAsync(_group.Id, new PanelOptions()));
        Assert.Contains("al menos 2 PRs", ex.Message);
    }

    [Fact]
    public void Store_suggests_prs_of_other_repos_with_the_same_branch()
    {
        CreateGroup(Api);
        _store.UpsertPullRequest(new PullRequest { Workspace = "acme", Repo = "web", Number = 2, SourceBranch = "feature/qr", State = "OPEN" });
        _store.UpsertPullRequest(new PullRequest { Workspace = "acme", Repo = "web", Number = 9, SourceBranch = "feature/otra", State = "OPEN" });
        _store.UpsertPullRequest(new PullRequest { Workspace = "acme", Repo = "app", Number = 4, SourceBranch = "feature/qr", State = "MERGED" });
        _store.UpsertPullRequest(new PullRequest { Workspace = "acme", Repo = "api", Number = 1, SourceBranch = "feature/qr" });

        Assert.Equal(["acme/web#2"], _store.GroupSuggestions(_group.Id).Select(p => p.Key));
        Assert.Equal(_group.Id, _store.FindGroup("FEATURE/QR")!.Id);
        Assert.Equal(_group.Id, _store.FindGroup(_group.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))!.Id);
        Assert.Throws<ArgumentException>(() => _store.CreateGroup("feature/qr"));

        _store.DeleteGroup(_group.Id);
        Assert.Empty(_store.ListGroups());
        Assert.NotNull(_store.FindPullRequest(Api));
    }

    [Fact]
    public void Parser_reads_the_pr_of_each_finding_and_splits_the_report()
    {
        string[] keys = ["acme/api#1", "acme/web#2"];
        var answer = Report("security", "block",
            Finding("SEC-1", "blocker", "api"), Finding("SEC-2", "minor", "acme/web#2"), Finding("SEC-3", "major", "otro/repo#5"), Finding("SEC-4", "info", null));

        var parsed = ReportParser.Parse(answer, "security", "SEC", keys);

        Assert.Equal(["acme/api#1", "acme/web#2", null, null], parsed.Report!.Findings.OrderBy(f => f.Id).Select(f => f.Pr));
        Assert.Contains(parsed.Warnings, w => w.Contains("otro/repo#5"));
        var web = parsed.Report.ForPr("acme/web#2");
        Assert.Equal(["SEC-2", "SEC-3", "SEC-4"], web.Findings.Select(f => f.Id).Order());
        Assert.Equal(Verdict.RequestChanges, web.Verdict);
        // Outside a group the field is ignored.
        Assert.All(ReportParser.Parse(answer, "security", "SEC").Report!.Findings, f => Assert.Null(f.Pr));
    }

    [Fact]
    public void Read_dirs_expand_to_one_argument_each()
    {
        var args = AgentAdapterBase.Expand(["--add-dir", "{contextDir}", "{readDirs}", "-p", "{prompt}"], null, "p", "/ctx", ["/a", "/b"]);
        Assert.Equal(["--add-dir", "/ctx", "/a", "/b", "-p", "p"], args);
        Assert.Equal(["--add-dir", "/ctx", "-p", "p"], AgentAdapterBase.Expand(["--add-dir", "{contextDir}", "{readDirs}", "-p", "{prompt}"], null, "p", "/ctx"));
    }
}
