using Prt.Core.Agents;
using Prt.Core.Config;

namespace Prt.Core.Tests;

public class AgentAdapterTests
{
    [Fact]
    public async Task Claude_presets_the_session_id_and_parses_print_output()
    {
        var runner = new FakeRunner
        {
            OnCaptured = _ => new ProcessResult(0, """{"type":"result","is_error":false,"result":"## Resumen\nok","session_id":"x"}""", ""),
        };
        var claude = new ClaudeAdapter(AgentSettings.DefaultFor("claude"), runner);

        var id = await claude.StartAsync("Revisá el PR", "/repo");
        var answer = await claude.AskAsync(id!, "/repo", "resumí");

        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(["--session-id", id!, "Revisá el PR"], runner.Interactive.Single().Args);
        Assert.Equal(["-p", "--resume", id!, "--output-format", "json", "resumí"], runner.Captured.Single().Args);
        Assert.Equal("## Resumen\nok", answer);
        Assert.Equal($"claude --resume {id}", claude.ResumeCommand(id!));
    }

    [Fact]
    public async Task Claude_reports_errors_from_print_mode()
    {
        var runner = new FakeRunner { OnCaptured = _ => new ProcessResult(0, """{"is_error":true,"result":"sesión no encontrada"}""", "") };
        var claude = new ClaudeAdapter(AgentSettings.DefaultFor("claude"), runner);

        var ex = await Assert.ThrowsAsync<AgentException>(() => claude.AskAsync("s", "/", "p"));
        Assert.Contains("sesión no encontrada", ex.Message);
    }

    [Fact]
    public async Task Resume_without_prompt_omits_the_prompt_argument()
    {
        var runner = new FakeRunner();
        await new ClaudeAdapter(AgentSettings.DefaultFor("claude"), runner).ResumeAsync("abc", "/repo", null);
        await new ClaudeAdapter(AgentSettings.DefaultFor("claude"), runner).ResumeAsync("abc", "/repo", "nuevos commits");

        Assert.Equal(["--resume", "abc"], runner.Interactive[0].Args);
        Assert.Equal(["--resume", "abc", "nuevos commits"], runner.Interactive[1].Args);
    }

    [Fact]
    public async Task Nonzero_exit_on_headless_run_throws()
    {
        var runner = new FakeRunner { OnCaptured = _ => new ProcessResult(2, "", "boom") };
        var ex = await Assert.ThrowsAsync<AgentException>(
            () => new CodexAdapter(AgentSettings.DefaultFor("codex"), runner, "/nonexistent").AskAsync("s", "/", "p"));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Codex_detects_the_new_rollout_matching_the_cwd()
    {
        using var home = new TempDir();
        var day = Path.Combine(home.Path, "sessions", "2026", "03", "10");
        Directory.CreateDirectory(day);
        File.WriteAllText(Path.Combine(day, "rollout-old-11111111-1111-1111-1111-111111111111.jsonl"),
            """{"type":"session_meta","payload":{"id":"11111111-1111-1111-1111-111111111111","cwd":"/repo"}}""");

        var runner = new FakeRunner
        {
            OnInteractive = _ =>
            {
                File.WriteAllText(Path.Combine(day, "rollout-a-22222222-2222-2222-2222-222222222222.jsonl"),
                    """{"type":"session_meta","payload":{"id":"22222222-2222-2222-2222-222222222222","cwd":"/other"}}""" + "\n");
                File.WriteAllText(Path.Combine(day, "rollout-b-33333333-3333-3333-3333-333333333333.jsonl"),
                    """{"type":"session_meta","payload":{"id":"33333333-3333-3333-3333-333333333333","cwd":"/repo/"}}""" + "\n");
            },
        };
        var codex = new CodexAdapter(AgentSettings.DefaultFor("codex"), runner, home.Path);

        var id = await codex.StartAsync("Revisá", "/repo");

        Assert.Equal("33333333-3333-3333-3333-333333333333", id);
        Assert.Equal(["Revisá"], runner.Interactive.Single().Args);
    }

    [Fact]
    public void Codex_reads_legacy_meta_and_falls_back_to_file_name()
    {
        using var dir = new TempDir();
        var legacy = Path.Combine(dir.Path, "rollout-x.jsonl");
        File.WriteAllText(legacy, """{"id":"44444444-4444-4444-4444-444444444444","timestamp":"t"}""");
        var garbage = Path.Combine(dir.Path, "rollout-2026-55555555-5555-5555-5555-555555555555.jsonl");
        File.WriteAllText(garbage, "not json");

        Assert.Equal("44444444-4444-4444-4444-444444444444", CodexAdapter.ReadMeta(legacy).Id);
        Assert.Equal("55555555-5555-5555-5555-555555555555", CodexAdapter.ReadMeta(garbage).Id);
    }

    [Fact]
    public async Task Codex_returns_the_last_agent_message()
    {
        var jsonl = string.Join('\n',
            """{"type":"thread.started","thread_id":"t"}""",
            """{"type":"item.completed","item":{"type":"agent_message","text":"primero"}}""",
            """{"type":"item.completed","item":{"type":"reasoning","text":"pienso"}}""",
            """{"type":"item.completed","item":{"type":"agent_message","text":"memo final"}}""",
            """{"type":"turn.completed"}""");
        var runner = new FakeRunner { OnCaptured = _ => new ProcessResult(0, jsonl, "") };
        var codex = new CodexAdapter(AgentSettings.DefaultFor("codex"), runner, "/nonexistent");

        Assert.Equal("memo final", await codex.AskAsync("s1", "/repo", "resumí"));
        Assert.Equal(["exec", "resume", "s1", "--json", "resumí"], runner.Captured.Single().Args);
    }

    [Fact]
    public async Task Opencode_detects_the_session_created_during_the_run()
    {
        var started = false;
        var runner = new FakeRunner
        {
            OnInteractive = _ => started = true,
            OnCaptured = args => args[0] == "session"
                ? new ProcessResult(0, started
                    ? """[{"id":"ses_new","time":{"updated":200}},{"id":"ses_old","time":{"updated":100}}]"""
                    : """[{"id":"ses_old","time":{"updated":100}}]""", "")
                : new ProcessResult(0, """
                    {"type":"step_start","part":{}}
                    {"type":"text","part":{"type":"text","text":"memo opencode"}}
                    """, ""),
        };
        var opencode = new OpencodeAdapter(AgentSettings.DefaultFor("opencode"), runner);

        var id = await opencode.StartAsync("Revisá", "/repo");

        Assert.Equal("ses_new", id);
        Assert.Equal(["--prompt", "Revisá"], runner.Interactive.Single().Args);
        Assert.Equal("memo opencode", await opencode.AskAsync(id!, "/repo", "resumí"));
    }

    [Fact]
    public void Opencode_session_list_falls_back_to_scraping_ids()
    {
        var sessions = OpencodeAdapter.ParseSessionList("ID           Title\nses_abc123   uno\nses_def456   dos\n");
        Assert.Equal(["ses_abc123", "ses_def456"], sessions.Select(s => s.Id));
    }

    [Fact]
    public void Config_overrides_merge_with_defaults()
    {
        var config = new PrtConfig();
        config.Agents["claude"] = new AgentSettings { Command = "/opt/claude", AskArgs = ["ask", "{sessionId}", "{prompt}"] };

        var merged = config.GetAgent("CLAUDE");

        Assert.Equal("/opt/claude", merged.Command);
        Assert.Equal(["ask", "{sessionId}", "{prompt}"], merged.AskArgs);
        Assert.Equal(["--resume", "{sessionId}"], merged.ResumeArgs);
    }
}
