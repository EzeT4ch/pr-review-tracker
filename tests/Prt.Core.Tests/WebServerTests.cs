using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Prt.Core.Agents;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Panel;
using Prt.Core.Storage;
using Prt.Web;

namespace Prt.Core.Tests;

public sealed class WebServerTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private static readonly PrRef Ref = new("acme", "api", 42);

    private readonly TempDir _dir = new();
    private readonly FakeRunner _runner = new();
    private readonly FakeBitbucket _bitbucket = new() { Commits = [new("c1full", "inicial", null)] };
    private readonly PrtConfig _config = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _db = null!;
    private int _saves;

    public async Task InitializeAsync()
    {
        _db = Path.Combine(_dir.Path, "prt.db");
        _config.VaultPath = Path.Combine(_dir.Path, "vault");
        // These tests use no git remote: the code source is covered by the repos tests.
        _config.ManagedClones = false;
        _runner.OnCaptured = _ => new ProcessResult(0, JsonSerializer.Serialize(new
        {
            result = """{"reviewer":"security","verdict":"request_changes","summary":"Falta validar.","findings":[{"id":"SEC-1","severity":"major","title":"Entrada sin validar","file":"src/Pay.cs","line":3,"description":"Se usa <b>id</b> sin validar.","recommendation":"Validar."}],"questions":["¿Es público?"]}""",
        }), "");

        var context = new WebContext
        {
            LoadConfig = () => _config,
            SaveConfig = _ => _saves++,
            ConfigDir = Path.Combine(_dir.Path, "cfg"),
            DatabasePath = _db,
            RunsRoot = Path.Combine(_dir.Path, "runs"),
            ReposRoot = Path.Combine(_dir.Path, "repos"),
            Bitbucket = _ => _bitbucket,
            Agent = (config, name) => AgentFactory.Create(name, config, _runner),
        };
        _app = PrtWebServer.Build(context, new WebOptions(0, TimeSpan.Zero, Token));
        await _app.StartAsync();
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{PrtWebServer.Port(_app)}"),
        };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private Task<HttpResponseMessage> Post(string path, params (string Key, string Value)[] fields) =>
        _http.PostAsync(path, new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Key, f.Value))));

    private async Task<Job> WaitForJob(PrRef prRef)
    {
        var jobs = _app.Services.GetService(typeof(JobRunner)) as JobRunner;
        for (var i = 0; i < 100; i++)
        {
            if (jobs!.Latest(prRef) is { Status: not JobStatus.Running } done)
                return done;
            await Task.Delay(50);
        }
        throw new TimeoutException("La tarea no terminó.");
    }

    [Fact]
    public async Task Track_registers_the_pr_and_shows_it()
    {
        var response = await Post("/track", ("csrf", Token), ("pr", "https://bitbucket.org/acme/api/pull-requests/42"), ("action", "sync"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/pr/acme/api/42", response.Headers.Location!.ToString());
        Assert.Equal(JobStatus.Succeeded, (await WaitForJob(Ref)).Status);

        var home = await _http.GetStringAsync("/");
        Assert.Contains("acme/api#42", home);
        Assert.Contains("Agregar pagos con QR", home);
        var page = await _http.GetStringAsync("/pr/acme/api/42");
        Assert.Contains("Sin análisis todavía.", page);
        Assert.Contains("prt review acme/api#42", page);
    }

    [Fact]
    public async Task Analyze_job_runs_the_panel_and_the_pages_show_findings()
    {
        await Post("/track", ("csrf", Token), ("pr", "acme/api#42"), ("action", "sync"));
        await WaitForJob(Ref);

        var response = await Post("/pr/acme/api/42/analyze", ("csrf", Token), ("reviewer", "security"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var job = await WaitForJob(Ref);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Contains(job.Log, l => l.Contains("Veredicto del panel: CAMBIOS REQUERIDOS"));
        var page = await _http.GetStringAsync("/pr/acme/api/42");
        Assert.Contains("SEC-1", page);
        Assert.Contains("¿Es público?", page);
        Assert.Contains("prt review acme/api#42 --reviewer security --agent claude", page);
        // Agent text is Markdown with HTML disabled: tags come out escaped.
        Assert.Contains("&lt;b&gt;id&lt;/b&gt;", page);
        Assert.DoesNotContain("<b>id</b>", page);

        using var store = new SqliteStore(_db);
        var runId = store.ListRuns(store.FindPullRequest(Ref)!.Id).Single().Run.Id;
        var report = await _http.GetStringAsync($"/pr/acme/api/42/run/{runId}");
        Assert.Contains("Entrada sin validar", report);
        Assert.Contains("Falta validar.", report);
    }

    [Fact]
    public async Task Memos_and_tags_are_stored_and_exported()
    {
        await Post("/track", ("csrf", Token), ("pr", "acme/api#42"), ("action", "sync"));
        await WaitForJob(Ref);

        await Post("/pr/acme/api/42/memo", ("csrf", Token), ("body", "Ok de negocio con Producto."), ("kind", "decision"));
        await Post("/pr/acme/api/42/tags", ("csrf", Token), ("add", "pagos arquitectura"));
        await Post("/pr/acme/api/42/tags", ("csrf", Token), ("remove", "pagos"));

        using var store = new SqliteStore(_db);
        var pr = store.FindPullRequest(Ref)!;
        Assert.Equal(MemoKinds.Decision, store.ListMemos(pr.Id).Single().Kind);
        Assert.Equal(["arquitectura"], store.ListTags(pr.Id));
        var note = Directory.GetFiles(Path.Combine(_config.VaultPath!, "PR Reviews", "acme", "api", "PR-42")).Single();
        Assert.Contains("Ok de negocio con Producto.", File.ReadAllText(note));
    }

    [Fact]
    public async Task Posts_without_the_csrf_token_are_rejected()
    {
        var response = await Post("/track", ("pr", "acme/api#42"), ("action", "analyze"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_runner.Captured);
    }

    [Fact]
    public async Task Only_answers_to_its_own_host()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "evil.example:80";

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(request)).StatusCode);

        var ok = await _http.GetAsync("/");
        Assert.Contains("default-src 'none'", ok.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Health_reports_memory_and_shutdown_needs_the_token()
    {
        var health = JsonDocument.Parse(await _http.GetStringAsync("/health")).RootElement;
        Assert.Equal(Environment.ProcessId, health.GetProperty("pid").GetInt32());
        Assert.False(health.GetProperty("asleep").GetBoolean());
        Assert.True(health.GetProperty("privateMb").GetDouble() > 0);
        Assert.True(health.GetProperty("workingSetMb").GetDouble() >= health.GetProperty("privateMb").GetDouble());

        Assert.Equal(HttpStatusCode.Forbidden, (await _http.PostAsync("/api/shutdown", null)).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shutdown");
        request.Headers.Add(PrtWebServer.TokenHeader, Token);
        Assert.Equal(HttpStatusCode.Accepted, (await _http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Repos_page_maps_and_unmaps_a_local_checkout()
    {
        await Post("/track", ("csrf", Token), ("pr", "acme/api#42"), ("action", "sync"));
        await WaitForJob(Ref);
        var checkout = Directory.CreateDirectory(Path.Combine(_dir.Path, "checkout")).FullName;

        var page = await _http.GetStringAsync("/repos");
        Assert.Contains("acme/api", page);
        Assert.Contains("solo el diff", page);

        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/repos/acme/api", ("action", "local"), ("path", checkout))).StatusCode);

        var missing = await (await Post("/repos/acme/api", ("csrf", Token), ("action", "local"), ("path", "/no/existe"))).Content.ReadAsStringAsync();
        Assert.Contains("no existe en esta máquina", missing);
        Assert.Empty(_config.RepoPaths);

        var mapped = await (await Post("/repos/acme/api", ("csrf", Token), ("action", "local"), ("path", checkout))).Content.ReadAsStringAsync();
        Assert.Contains("usa ahora el checkout local", mapped);
        Assert.Equal(checkout, _config.RepoPaths["acme/api"]);
        Assert.Contains($"checkout local <code>{checkout}</code>", await _http.GetStringAsync("/pr/acme/api/42"));

        await Post("/repos/acme/api", ("csrf", Token), ("action", "managed"));
        Assert.Empty(_config.RepoPaths);
        Assert.Equal(2, _saves);
    }

    [Fact]
    public async Task Diff_page_shows_the_changes_with_findings_on_their_lines()
    {
        _bitbucket.Diff = UnifiedDiffTests.Sample;
        _runner.OnCaptured = _ => new ProcessResult(0, JsonSerializer.Serialize(new
        {
            result = """{"reviewer":"security","verdict":"block","summary":"s","findings":[{"id":"SEC-1","severity":"blocker","title":"SQL injection","file":"src/Pay.cs","line":11,"description":"d","recommendation":"Usar parámetros."},{"id":"SEC-2","severity":"minor","title":"Sin línea","file":"src/Nuevo.cs"}]}""",
        }), "");
        await Post("/track", ("csrf", Token), ("pr", "acme/api#42"), ("action", "analyze"));
        await WaitForJob(Ref);

        var page = await _http.GetStringAsync("/pr/acme/api/42/diff");

        Assert.Contains("5 archivo(s)", page);
        Assert.Contains("docs/old.md → docs/new.md", page);
        Assert.Contains("id=\"file-0-L11\"", page);
        // The finding goes right after its line, before the next one.
        var line11 = page.IndexOf("id=\"file-0-L11\"", StringComparison.Ordinal);
        Assert.InRange(page.IndexOf("SQL injection", line11, StringComparison.Ordinal), line11, page.IndexOf("id=\"file-0-L12\"", StringComparison.Ordinal));
        Assert.Contains("Sin línea", page);
        // Code is escaped.
        Assert.Contains("// &lt;script&gt;", page);
        Assert.DoesNotContain("// <script>", page);

        // The run keeps the diff it analysed, even if the PR changes later.
        _bitbucket.Diff = "diff --git a/otro.cs b/otro.cs\n";
        using var store = new SqliteStore(_db);
        var runId = store.ListRuns(store.FindPullRequest(Ref)!.Id).Single().Run.Id;
        var runDiff = await _http.GetStringAsync($"/pr/acme/api/42/run/{runId}/diff");
        Assert.Contains("src/Pay.cs", runDiff);
        Assert.Contains($"/pr/acme/api/42/run/{runId}/diff", await _http.GetStringAsync($"/pr/acme/api/42/run/{runId}"));
        Assert.Contains("/pr/acme/api/42/diff", await _http.GetStringAsync("/pr/acme/api/42"));

        // Cached for a couple of minutes unless refreshed.
        Assert.Contains("src/Pay.cs", await _http.GetStringAsync("/pr/acme/api/42/diff"));
        Assert.Contains("otro.cs", await _http.GetStringAsync("/pr/acme/api/42/diff?refresh=1"));
    }

    [Fact]
    public async Task Diff_page_falls_back_to_the_last_analysis_when_offline()
    {
        _bitbucket.Diff = UnifiedDiffTests.Sample;
        await Post("/track", ("csrf", Token), ("pr", "acme/api#42"), ("action", "analyze"));
        await WaitForJob(Ref);
        _bitbucket.Diff = null;

        var page = await _http.GetStringAsync("/pr/acme/api/42/diff?refresh=1");

        Assert.Contains("No se pudo consultar Bitbucket", page);
        Assert.Contains("src/Pay.cs", page);
    }

    [Fact]
    public async Task Reviewers_are_edited_per_project_or_globally()
    {
        await Post("/track", ("csrf", Token), ("pr", "acme/api#42"), ("action", "sync"));
        await WaitForJob(Ref);
        var cfg = Path.Combine(_dir.Path, "cfg");

        var list = await _http.GetStringAsync("/repos/acme/api/reviewers");
        Assert.Contains("Revisores de acme/api", list);
        Assert.Contains("incorporado", list);
        var form = await _http.GetStringAsync("/repos/acme/api/reviewers/edit?name=performance");
        Assert.Contains("Personal para este proyecto", form);
        Assert.DoesNotContain("value=\"repo:acme/api\"", form);

        // Disable performance and switch security to codex, only for this project.
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/repos/acme/api/reviewers/save", ("name", "performance"))).StatusCode);
        var saved = await (await Post("/repos/acme/api/reviewers/save", ("csrf", Token), ("new", "0"), ("name", "performance"),
            ("title", "Performance"), ("id_prefix", "PERF"), ("order", "3"), ("agent", ""), ("instructions", "Solo consultas SQL."), ("target", "user:acme/api"))).Content.ReadAsStringAsync();
        Assert.Contains("guardado en", saved);
        Assert.True(File.Exists(Path.Combine(cfg, "projects", "acme", "api", "reviewers", "performance.md")));

        var project = ProfileLoader.Load(ProfileLoader.LayerDirs("acme/api", null, cfg));
        Assert.DoesNotContain(project.Select(null), r => r.Name == "performance");
        Assert.Equal("Solo consultas SQL.", project.Reviewers.Single(r => r.Name == "performance").Instructions);
        Assert.Contains("personal", await _http.GetStringAsync("/repos/acme/api/reviewers"));
        // Other projects and the global view are untouched.
        Assert.Contains(ProfileLoader.Load([cfg]).Select(null), r => r.Name == "performance");

        // New reviewer, invalid first, then valid.
        var invalid = await (await Post("/repos/acme/api/reviewers/save", ("csrf", Token), ("new", "1"), ("name", "PCI!"),
            ("title", "PCI"), ("id_prefix", "PCI"), ("instructions", "x"), ("target", "user:acme/api"), ("enabled", "on"))).Content.ReadAsStringAsync();
        Assert.Contains("minúsculas", invalid);
        await Post("/repos/acme/api/reviewers/save", ("csrf", Token), ("new", "1"), ("name", "pci"),
            ("title", "Cumplimiento PCI"), ("id_prefix", "pci"), ("order", "5"), ("agent", "codex"), ("instructions", "Datos de tarjeta."), ("target", "user:acme/api"), ("enabled", "on"));
        var pci = ProfileLoader.Load(ProfileLoader.LayerDirs("acme/api", null, cfg)).Reviewers.Single(r => r.Name == "pci");
        Assert.Equal(("PCI", "codex", true), (pci.IdPrefix, pci.Agent, pci.Enabled));

        // Removing the personalization brings back the inherited reviewer.
        await Post("/repos/acme/api/reviewers/remove", ("csrf", Token), ("name", "performance"), ("target", "user:acme/api"));
        Assert.Contains(ProfileLoader.Load(ProfileLoader.LayerDirs("acme/api", null, cfg)).Select(null), r => r.Name == "performance");

        // Global edits land in the config folder and apply to every project.
        await Post("/reviewers/save", ("csrf", Token), ("new", "0"), ("name", "bugs"), ("title", "Bugs"), ("id_prefix", "BUG"),
            ("order", "2"), ("agent", "opencode"), ("instructions", "Bugs globales."), ("target", "global"), ("enabled", "on"));
        Assert.True(File.Exists(Path.Combine(cfg, "reviewers", "bugs.md")));
        Assert.Equal("opencode", ProfileLoader.Load(ProfileLoader.LayerDirs("otro/repo", null, cfg)).Reviewers.Single(r => r.Name == "bugs").Agent);
        Assert.Contains("global (heredado)", await _http.GetStringAsync("/repos/acme/api/reviewers"));
    }

    [Fact]
    public async Task Global_view_can_save_a_reviewer_for_a_registered_project()
    {
        await Post("/track", ("csrf", Token), ("pr", "acme/api#42"), ("action", "sync"));
        await WaitForJob(Ref);
        var cfg = Path.Combine(_dir.Path, "cfg");

        var form = await _http.GetStringAsync("/reviewers/edit?new=1");
        Assert.Contains("value=\"global\"", form);
        Assert.Contains("value=\"user:acme/api\"", form);
        Assert.DoesNotContain("value=\"repo:acme/api\"", form);

        var response = await Post("/reviewers/save", ("csrf", Token), ("new", "1"), ("name", "pci"), ("title", "PCI"),
            ("id_prefix", "PCI"), ("order", "5"), ("agent", ""), ("instructions", "Datos de tarjeta."), ("target", "user:acme/api"), ("enabled", "on"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadAsStringAsync();

        Assert.Contains("Revisores de acme/api", saved);
        Assert.True(File.Exists(Path.Combine(cfg, "projects", "acme", "api", "reviewers", "pci.md")));
        Assert.DoesNotContain(ProfileLoader.Load([cfg]).Reviewers, r => r.Name == "pci");
        Assert.Contains(ProfileLoader.Load(ProfileLoader.LayerDirs("acme/api", null, cfg)).Reviewers, r => r.Name == "pci");

        // Same name again in that project is a duplicate; globally it is not.
        var duplicate = await (await Post("/reviewers/save", ("csrf", Token), ("new", "1"), ("name", "pci"), ("title", "PCI"),
            ("id_prefix", "PCI"), ("instructions", "x"), ("target", "user:acme/api"), ("enabled", "on"))).Content.ReadAsStringAsync();
        Assert.Contains("Ya existe un revisor &#39;pci&#39; en acme/api", duplicate);
        await Post("/reviewers/save", ("csrf", Token), ("new", "1"), ("name", "pci"), ("title", "PCI global"),
            ("id_prefix", "PCI"), ("instructions", "x"), ("target", "global"), ("enabled", "on"));
        Assert.True(File.Exists(Path.Combine(cfg, "reviewers", "pci.md")));

        // The global edit form offers removing the project's personalization too.
        var edit = await _http.GetStringAsync("/reviewers/edit?name=pci");
        Assert.Contains("Quitar la personalización personal de acme/api", edit);
        await Post("/reviewers/remove", ("csrf", Token), ("name", "pci"), ("target", "user:acme/api"));
        Assert.False(File.Exists(Path.Combine(cfg, "projects", "acme", "api", "reviewers", "pci.md")));
        Assert.True(File.Exists(Path.Combine(cfg, "reviewers", "pci.md")));
    }

    [Fact]
    public async Task Repo_layer_needs_a_local_checkout()
    {
        await Post("/track", ("csrf", Token), ("pr", "acme/api#42"), ("action", "sync"));
        await WaitForJob(Ref);
        var checkout = Directory.CreateDirectory(Path.Combine(_dir.Path, "checkout")).FullName;

        var rejected = await (await Post("/repos/acme/api/reviewers/save", ("csrf", Token), ("new", "0"), ("name", "security"),
            ("title", "Seguridad"), ("id_prefix", "SEC"), ("instructions", "x"), ("target", "repo:acme/api"), ("enabled", "on"))).Content.ReadAsStringAsync();
        Assert.Contains("Elegí dónde guardar", rejected);

        _config.RepoPaths["acme/api"] = checkout;
        Assert.Contains("value=\"repo:acme/api\"", await _http.GetStringAsync("/repos/acme/api/reviewers/edit?name=security"));
        await Post("/repos/acme/api/reviewers/save", ("csrf", Token), ("new", "0"), ("name", "security"),
            ("title", "Seguridad del equipo"), ("id_prefix", "SEC"), ("instructions", "Reglas del equipo."), ("target", "repo:acme/api"), ("enabled", "on"));

        Assert.True(File.Exists(Path.Combine(checkout, ".prt", "reviewers", "security.md")));
        Assert.Contains("repo (.prt/)", await _http.GetStringAsync("/repos/acme/api/reviewers"));
    }

    [Fact]
    public async Task Invalid_pr_reference_shows_an_error()
    {
        var html = await (await Post("/track", ("csrf", Token), ("pr", "no-es-un-pr"), ("action", "sync"))).Content.ReadAsStringAsync();
        Assert.Contains("no es un PR válido", html);
    }
}

public class IdleMonitorTests
{
    [Fact]
    public async Task Sleeps_when_idle_and_wakes_on_activity()
    {
        var busy = false;
        var monitor = new IdleMonitor(TimeSpan.FromMilliseconds(150), () => busy);
        using var cts = new CancellationTokenSource();
        var loop = monitor.RunAsync(cts.Token);

        await WaitUntil(() => monitor.Asleep);
        Assert.Equal(1, monitor.Sleeps);

        monitor.Touch();
        Assert.False(monitor.Asleep);
        await WaitUntil(() => monitor.Asleep);
        Assert.Equal(2, monitor.Sleeps);

        await cts.CancelAsync();
        await loop;
    }

    [Fact]
    public async Task Does_not_sleep_while_busy_or_when_disabled()
    {
        var monitor = new IdleMonitor(TimeSpan.FromMilliseconds(50), () => true);
        using var cts = new CancellationTokenSource();
        var loop = monitor.RunAsync(cts.Token);
        await Task.Delay(300);
        Assert.False(monitor.Asleep);
        await cts.CancelAsync();
        await loop;

        var disabled = new IdleMonitor(TimeSpan.Zero, () => false);
        await disabled.RunAsync(CancellationToken.None);
        Assert.False(disabled.Asleep);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }
}
