using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prt.Core.Domain;
using Prt.Core.Export;
using Prt.Core.Panel;
using Prt.Core.Review;

namespace Prt.Web;

/// <summary>
/// The web panel: Kestrel with the slim builder, server-rendered pages, bound to 127.0.0.1 only.
/// </summary>
public static class PrtWebServer
{
    public const string TokenHeader = "X-Prt-Token";

    public static WebApplication Build(WebContext context, WebOptions options)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, options.Port);
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = 32;
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
        });

        var jobs = new JobRunner();
        var idle = new IdleMonitor(options.SleepAfter, () => jobs.Busy, context.Clock);
        builder.Services.AddSingleton(jobs);
        builder.Services.AddSingleton(idle);
        builder.Services.AddHostedService(_ => new IdleService(idle));

        var app = builder.Build();
        var started = DateTimeOffset.Now;
        var pages = new Pages(context, jobs, options.Token);

        app.Use(async (http, next) =>
        {
            // Only answer to our own origin: blocks DNS rebinding from a page in the browser.
            if (!IsLocalHost(http.Request.Host, Port(app)))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            idle.Touch();
            var headers = http.Response.Headers;
            headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; form-action 'self'; frame-ancestors 'none'";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";

            if (HttpMethods.IsPost(http.Request.Method) && !http.Request.Path.StartsWithSegments("/api"))
            {
                var form = await http.Request.ReadFormAsync();
                if (form["csrf"] != options.Token)
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }

            try
            {
                await next(http);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
            {
                await Results.Content(
                    Html.Page("Error", $"<h1>No se pudo completar</h1><p>{Html.E(ex.Message)}</p><p><a href=\"/\">Volver</a></p>"),
                    "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(http);
            }
        });

        app.MapGet("/", (HttpContext http) => Write(http, pages.Home(http.Request.Query["state"], http.Request.Query["repo"], http.Request.Query["q"], null)));

        app.MapPost("/track", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            if (!PrRef.TryParse(form["pr"], out var prRef))
                return Write(http, pages.Home(null, null, null, $"'{form["pr"]}' no es un PR válido. Usá la URL de Bitbucket o workspace/repo#numero."));
            StartJob(form["action"] == "analyze" ? "analyze" : "sync", prRef, new PanelOptions());
            return Redirect(http, PrUrl(prRef));
        });

        app.MapGet("/pr/{ws}/{repo}/{n:int}", (HttpContext http, string ws, string repo, int n) =>
            Write(http, pages.PullRequest(new PrRef(ws, repo, n)), notFound: !Exists(new PrRef(ws, repo, n))));

        app.MapGet("/pr/{ws}/{repo}/{n:int}/run/{id:long}", (HttpContext http, string ws, string repo, int n, long id) =>
            Write(http, pages.Run(new PrRef(ws, repo, n), id)));

        app.MapPost("/pr/{ws}/{repo}/{n:int}/memo", async (HttpContext http, string ws, string repo, int n) =>
        {
            var prRef = new PrRef(ws, repo, n);
            var form = await http.Request.ReadFormAsync();
            var body = form["body"].ToString();
            if (body.Trim().Length > 0)
            {
                var config = context.LoadConfig();
                using var store = context.OpenStore();
                var pr = store.FindPullRequest(prRef) ?? throw new ArgumentException($"{prRef.Key} no está registrado.");
                store.AddMemo(pr.Id, null, form["kind"] == "decision" ? MemoKinds.Decision : MemoKinds.UserNote, body);
                context.Exporter(config)?.Export(store, pr.Id);
            }
            return Redirect(http, PrUrl(prRef) + "#timeline");
        });

        app.MapPost("/pr/{ws}/{repo}/{n:int}/tags", async (HttpContext http, string ws, string repo, int n) =>
        {
            var prRef = new PrRef(ws, repo, n);
            var form = await http.Request.ReadFormAsync();
            var config = context.LoadConfig();
            using var store = context.OpenStore();
            var pr = store.FindPullRequest(prRef) ?? throw new ArgumentException($"{prRef.Key} no está registrado.");
            store.AddTags(pr.Id, form["add"].ToString().Split([' ', ','], StringSplitOptions.RemoveEmptyEntries));
            if (form["remove"].ToString() is { Length: > 0 } remove)
                store.RemoveTags(pr.Id, [remove]);
            context.Exporter(config)?.Export(store, pr.Id);
            return Redirect(http, PrUrl(prRef));
        });

        app.MapPost("/pr/{ws}/{repo}/{n:int}/sync", (HttpContext http, string ws, string repo, int n) =>
        {
            var prRef = new PrRef(ws, repo, n);
            StartJob("sync", prRef, new PanelOptions());
            return Redirect(http, PrUrl(prRef));
        });

        app.MapPost("/pr/{ws}/{repo}/{n:int}/analyze", async (HttpContext http, string ws, string repo, int n) =>
        {
            var prRef = new PrRef(ws, repo, n);
            var form = await http.Request.ReadFormAsync();
            var reviewers = form["reviewer"].Where(r => !string.IsNullOrEmpty(r)).Select(r => r!).ToList();
            var agent = form["agent"].ToString() is { Length: > 0 } a ? a : null;
            StartJob("analyze", prRef, new PanelOptions(reviewers.Count > 0 ? reviewers : null, agent, form["new"] == "on"));
            return Redirect(http, PrUrl(prRef));
        });

        app.MapGet("/jobs", (HttpContext http) => Write(http, pages.Jobs()));

        app.MapGet("/health", (HttpContext http) =>
        {
            using var process = Process.GetCurrentProcess();
            return Results.Json(new
            {
                status = "ok",
                pid = Environment.ProcessId,
                asleep = idle.Asleep,
                sleeps = idle.Sleeps,
                lastActivity = idle.LastActivity,
                startedAt = started,
                privateMb = Math.Round(PrivateBytes(process) / 1024d / 1024d, 1),
                workingSetMb = Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
                managedMb = Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 1),
                runningJobs = jobs.All.Count(j => j.Status == JobStatus.Running),
            });
        });

        app.MapPost("/api/shutdown", (HttpContext http, IHostApplicationLifetime lifetime) =>
        {
            if (http.Request.Headers[TokenHeader] != options.Token)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            lifetime.StopApplication();
            return Results.Accepted();
        });

        return app;

        bool Exists(PrRef prRef)
        {
            using var store = context.OpenStore();
            return store.FindPullRequest(prRef) is not null;
        }

        void StartJob(string kind, PrRef prRef, PanelOptions panelOptions) =>
            jobs.Start(kind, prRef, job => kind == "analyze" ? AnalyzeAsync(job, panelOptions) : SyncAsync(job));

        async Task SyncAsync(Job job)
        {
            var config = context.LoadConfig();
            using var store = context.OpenStore();
            job.Add("Consultando Bitbucket…");
            var (pr, _) = await new PullRequestSync(store, context.Bitbucket(config), job).RefreshAsync(job.Pr);
            context.Exporter(config)?.Export(store, pr.Id);
            job.Add($"{pr.Key}: {pr.State} · {pr.Title}");
        }

        async Task AnalyzeAsync(Job job, PanelOptions panelOptions)
        {
            var config = context.LoadConfig();
            using var store = context.OpenStore();
            var panel = new PanelService(config, store, context.Bitbucket(config), name => context.Agent(config, name),
                context.Exporter(config), job, context.RunsRoot, context.Clock);
            var outcome = await panel.AnalyzeAsync(job.Pr, panelOptions);
            job.Add($"Veredicto del panel: {(outcome.Verdict is { } v ? ReviewStandard.Label(v) : "sin reportes válidos")}");
        }
    }

    /// <summary>The port actually bound (differs from the requested one when it was 0).</summary>
    public static int Port(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        return address is null ? 0 : new Uri(address).Port;
    }

    /// <summary>
    /// Memory only this process uses. The working set also counts the runtime's DLLs mapped from disk,
    /// which are shared with other .NET processes and reclaimable by the OS.
    /// </summary>
    private static long PrivateBytes(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var line = File.ReadLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("RssAnon:", StringComparison.Ordinal));
                if (line is not null && long.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out var kb))
                    return kb * 1024;
            }
            catch (IOException)
            {
                // Fall back below.
            }
        }
        return process.PrivateMemorySize64;
    }

    private static bool IsLocalHost(HostString host, int port) =>
        host.Port == port && host.Host is "127.0.0.1" or "localhost";

    internal static string PrUrl(PrRef prRef) => Html.Url("pr", prRef.Workspace, prRef.Repo, prRef.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static IResult Redirect(HttpContext http, string url) => Results.Redirect(url, permanent: false, preserveMethod: false);

    private static IResult Write(HttpContext http, string html, bool notFound = false) =>
        Results.Content(html, "text/html; charset=utf-8", statusCode: notFound ? StatusCodes.Status404NotFound : StatusCodes.Status200OK);

    private sealed class IdleService(IdleMonitor monitor) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => monitor.RunAsync(stoppingToken);
    }
}
