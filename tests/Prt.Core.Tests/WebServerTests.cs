using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Prt.Core.Agents;
using Prt.Core.Config;
using Prt.Core.Domain;
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

    public async Task InitializeAsync()
    {
        _db = Path.Combine(_dir.Path, "prt.db");
        _config.VaultPath = Path.Combine(_dir.Path, "vault");
        _runner.OnCaptured = _ => new ProcessResult(0, JsonSerializer.Serialize(new
        {
            result = """{"reviewer":"security","verdict":"request_changes","summary":"Falta validar.","findings":[{"id":"SEC-1","severity":"major","title":"Entrada sin validar","file":"src/Pay.cs","line":3,"description":"Se usa <b>id</b> sin validar.","recommendation":"Validar."}],"questions":["¿Es público?"]}""",
        }), "");

        var context = new WebContext
        {
            LoadConfig = () => _config,
            DatabasePath = _db,
            RunsRoot = Path.Combine(_dir.Path, "runs"),
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
