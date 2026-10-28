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
using Prt.Core.Bitbucket;
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
        var diffs = new PrCache<string>(context.Clock);
        var comments = new PrCache<IReadOnlyList<PrComment>>(context.Clock);
        var idle = new IdleMonitor(options.SleepAfter, () => jobs.Busy, context.Clock, () =>
        {
            diffs.Clear();
            comments.Clear();
        });
        builder.Services.AddSingleton(jobs);
        builder.Services.AddSingleton(idle);
        builder.Services.AddHostedService(_ => new IdleService(idle));

        var app = builder.Build();
        var started = DateTimeOffset.Now;
        var pages = new Pages(context, jobs, options.Token, diffs, comments);

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
            StartJob(form["action"] == "analyze" ? "analyze" : "sync", prRef, new PanelOptions(Reviewers(form), AllWhenEmpty: true));
            return Redirect(http, PrUrl(prRef));
        });

        app.MapGet("/pr/{ws}/{repo}/{n:int}", (HttpContext http, string ws, string repo, int n) =>
            Write(http, pages.PullRequest(new PrRef(ws, repo, n)), notFound: !Exists(new PrRef(ws, repo, n))));

        app.MapGet("/pr/{ws}/{repo}/{n:int}/run/{id:long}", (HttpContext http, string ws, string repo, int n, long id) =>
            Write(http, pages.Run(new PrRef(ws, repo, n), id)));

        app.MapGet("/pr/{ws}/{repo}/{n:int}/diff", async (HttpContext http, string ws, string repo, int n) =>
        {
            var query = http.Request.Query;
            var request = new CommentRequest(
                Target: query["c"].ToString() is { Length: > 0 } c ? c : null,
                FindingId: query["f"].ToString() is { Length: > 0 } f ? f : null,
                ReplyTo: long.TryParse(query["reply"], out var reply) ? reply : null);
            return Write(http, await pages.Diff(new PrRef(ws, repo, n), query["refresh"] == "1", request));
        });

        app.MapPost("/pr/{ws}/{repo}/{n:int}/comments", async (HttpContext http, string ws, string repo, int n) =>
        {
            var prRef = new PrRef(ws, repo, n);
            var form = await http.Request.ReadFormAsync();
            var body = form["body"].ToString();
            var path = form["path"].ToString() is { Length: > 0 } p ? p : null;
            int? line = int.TryParse(form["line"], out var l) ? l : null;
            long? parent = long.TryParse(form["parent"], out var pid) ? pid : null;
            var oldSide = form["side"] == "old";
            var finding = form["finding"].ToString() is { Length: > 0 } fid ? fid : null;
            var back = form["back"].ToString();
            var request = parent is not null
                ? new CommentRequest(ReplyTo: parent, Draft: body)
                : new CommentRequest(Target: back, FindingId: finding, Draft: body);

            try
            {
                var config = context.LoadConfig();
                PrLineComment posted;
                using (var store = context.OpenStore())
                {
                    posted = await new CommentService(store, context.Bitbucket(config), context.Exporter(config))
                        .PostAsync(prRef, new NewComment(body, path, line, oldSide, parent), finding);
                }
                comments.Invalidate(prRef);
                var file = int.TryParse(back.Split('-')[0], out var index) ? index : 0;
                var anchor = parent is not null ? $"comment-{posted.RemoteId}"
                    : line is { } at ? Pages.LineAnchor(file, oldSide, at)
                    : $"file-{file}";
                return Redirect(http, PrUrl(prRef) + "/diff#" + anchor);
            }
            catch (Exception ex) when (ex is BitbucketException or HttpRequestException or ArgumentException)
            {
                return Write(http, await pages.Diff(prRef, false, request with { Error = ex.Message }));
            }
        });

        app.MapGet("/pr/{ws}/{repo}/{n:int}/run/{id:long}/diff", (HttpContext http, string ws, string repo, int n, long id) =>
            Write(http, pages.RunDiff(new PrRef(ws, repo, n), id)));

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
            var agent = form["agent"].ToString() is { Length: > 0 } a ? a : null;
            StartJob("analyze", prRef, new PanelOptions(Reviewers(form), agent, form["new"] == "on", AllWhenEmpty: true));
            return Redirect(http, PrUrl(prRef));
        });

        app.MapGet("/groups", (HttpContext http) => Write(http, pages.Groups(null)));

        app.MapPost("/groups", async Task<IResult> (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            if (!TryParseRefs(form["prs"], out var refs, out var invalid))
                return Write(http, pages.Groups($"'{invalid}' no es un PR válido. Usá la URL de Bitbucket o workspace/repo#numero."));
            PrGroup group;
            using (var store = context.OpenStore())
                group = store.CreateGroup(form["name"].ToString(), form["description"].ToString());
            AddMembers(group.Id, refs);
            return Redirect(http, Pages.GroupUrl(group.Id));
        });

        app.MapGet("/groups/{id:long}", (HttpContext http, long id) => Write(http, pages.Group(id, null)));

        app.MapGet("/groups/{id:long}/run/{runId:long}", (HttpContext http, long id, long runId) => Write(http, pages.GroupRunReport(id, runId)));

        app.MapPost("/groups/{id:long}/members", async (HttpContext http, long id) =>
        {
            var form = await http.Request.ReadFormAsync();
            if (long.TryParse(form["remove"], out var prId))
            {
                var config = context.LoadConfig();
                using var store = context.OpenStore();
                store.RemoveGroupMember(id, prId);
                context.Exporter(config)?.ExportGroup(store, id);
                context.Exporter(config)?.Export(store, prId);
                return Redirect(http, Pages.GroupUrl(id));
            }
            if (!TryParseRefs(form["add"], out var refs, out var invalid))
                return Write(http, pages.Group(id, $"'{invalid}' no es un PR válido. Usá la URL de Bitbucket o workspace/repo#numero."));
            if (jobs.Running(GroupJobs.Ref(id)) is not null)
                return Write(http, pages.Group(id, "Hay una tarea en curso para este grupo: agregá los PRs cuando termine."));
            AddMembers(id, refs);
            return Redirect(http, Pages.GroupUrl(id));
        });

        app.MapPost("/groups/{id:long}/analyze", async (HttpContext http, long id) =>
        {
            var form = await http.Request.ReadFormAsync();
            var agent = form["agent"].ToString() is { Length: > 0 } a ? a : null;
            var options = new PanelOptions(Reviewers(form), agent, form["new"] == "on", AllWhenEmpty: true);
            jobs.Start(GroupJobs.Analyze, GroupJobs.Ref(id), job => AnalyzeGroupAsync(job, id, options));
            return Redirect(http, Pages.GroupUrl(id));
        });

        app.MapPost("/groups/{id:long}/edit", async (HttpContext http, long id) =>
        {
            var form = await http.Request.ReadFormAsync();
            var config = context.LoadConfig();
            using var store = context.OpenStore();
            store.UpdateGroup(id, form["name"].ToString(), form["description"].ToString());
            context.Exporter(config)?.ExportGroup(store, id);
            return Redirect(http, Pages.GroupUrl(id));
        });

        app.MapPost("/groups/{id:long}/delete", (HttpContext http, long id) =>
        {
            var config = context.LoadConfig();
            using var store = context.OpenStore();
            var members = store.ListGroupMembers(id);
            store.DeleteGroup(id);
            if (context.Exporter(config) is { } exporter)
            {
                foreach (var pr in members)
                    exporter.Export(store, pr.Id);
            }
            return Redirect(http, "/groups");
        });

        app.MapGet("/jobs", (HttpContext http) => Write(http, pages.Jobs()));

        app.MapGet("/reviewers", (HttpContext http) => Write(http, pages.Reviewers(null, null)));
        app.MapGet("/reviewers/edit", (HttpContext http) =>
            Write(http, pages.ReviewerForm(null, http.Request.Query["name"], http.Request.Query["new"] == "1")));
        // Explicit Task<IResult>: an async lambda taking only HttpContext would bind to RequestDelegate and drop the result.
        app.MapPost("/reviewers/save", async Task<IResult> (HttpContext http) => Write(http, pages.SaveReviewer(null, await http.Request.ReadFormAsync())));
        app.MapPost("/reviewers/remove", async Task<IResult> (HttpContext http) => Write(http, pages.RemoveReviewer(null, await http.Request.ReadFormAsync())));

        app.MapGet("/repos/{ws}/{repo}/reviewers", (HttpContext http, string ws, string repo) => Write(http, pages.Reviewers($"{ws}/{repo}", null)));
        app.MapGet("/repos/{ws}/{repo}/reviewers/edit", (HttpContext http, string ws, string repo) =>
            Write(http, pages.ReviewerForm($"{ws}/{repo}", http.Request.Query["name"], http.Request.Query["new"] == "1")));
        app.MapPost("/repos/{ws}/{repo}/reviewers/save", async (HttpContext http, string ws, string repo) =>
            Write(http, pages.SaveReviewer($"{ws}/{repo}", await http.Request.ReadFormAsync())));
        app.MapPost("/repos/{ws}/{repo}/reviewers/remove", async (HttpContext http, string ws, string repo) =>
            Write(http, pages.RemoveReviewer($"{ws}/{repo}", await http.Request.ReadFormAsync())));

        app.MapGet("/repos", (HttpContext http) => Write(http, pages.Repos(null, null)));

        app.MapPost("/repos/{ws}/{repo}", async (HttpContext http, string ws, string repo) =>
        {
            var repoKey = $"{ws}/{repo}";
            var form = await http.Request.ReadFormAsync();
            var config = context.LoadConfig();
            switch (form["action"].ToString())
            {
                case "local":
                {
                    var path = form["path"].ToString().Trim().Trim('"');
                    if (path.Length == 0 || !Directory.Exists(path))
                        return Write(http, pages.Repos(repoKey, $"La carpeta '{path}' no existe en esta máquina."));
                    path = Path.GetFullPath(path);
                    config.RepoPaths[repoKey] = path;
                    context.SaveConfig(config);
                    var warning = Prt.Core.Config.GitCheckout.FindRoot(path) is { } root && !Prt.Core.Config.GitCheckout.HasBitbucketRemote(root, repoKey)
                        ? $" Ojo: el remoto de esa carpeta no es bitbucket.org/{repoKey}."
                        : "";
                    return Write(http, pages.Repos(repoKey, $"{repoKey} usa ahora el checkout local {path}.{warning}"));
                }
                case "managed":
                    config.RepoPaths.Remove(repoKey);
                    context.SaveConfig(config);
                    return Write(http, pages.Repos(repoKey, config.ManagedClones
                        ? $"{repoKey} usa el clon gestionado por prt."
                        : $"{repoKey} ya no tiene checkout local (los clones gestionados están desactivados en el config)."));
                case "update":
                    StartRepoJob(repoKey, config);
                    return Redirect(http, "/jobs");
                case "delete":
                    context.Managed(config).Delete(repoKey);
                    return Write(http, pages.Repos(repoKey, $"Clon gestionado de {repoKey} borrado; se vuelve a crear en el próximo análisis."));
                default:
                    return Write(http, pages.Repos(null, null));
            }
        });

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

        // Updates the managed clone for every destination branch seen in the repo's PRs.
        void StartRepoJob(string repoKey, Prt.Core.Config.PrtConfig config)
        {
            var parts = repoKey.Split('/');
            jobs.Start("clone", new PrRef(parts[0], parts[1], 0), async job =>
            {
                List<PullRequest> prs;
                using (var store = context.OpenStore())
                    prs = store.ListPullRequests(repoKey: repoKey).ToList();
                var managed = context.Managed(config);
                foreach (var branch in prs.Select(p => p.DestinationBranch).Where(b => b.Length > 0).Distinct())
                {
                    var info = await managed.PrepareAsync(job.Pr, branch, null, job);
                    job.Add($"Listo: {info.Describe()}.");
                }
            });
        }

        void StartJob(string kind, PrRef prRef, PanelOptions panelOptions) =>
            jobs.Start(kind, prRef, job => kind == "analyze" ? AnalyzeAsync(job, panelOptions) : SyncAsync(job));

        async Task SyncAsync(Job job)
        {
            var config = context.LoadConfig();
            using var store = context.OpenStore();
            job.Add("Consultando el PR y sus commits en Bitbucket…");
            var (pr, commits) = await new PullRequestSync(store, context.Bitbucket(config), job).RefreshAsync(job.Pr);
            job.Add($"{pr.Key}: {pr.State} · {pr.Title} · {commits.Count} commit(s)");
            if (context.Exporter(config) is { } exporter)
            {
                exporter.Export(store, pr.Id);
                job.Add("Nota de Obsidian actualizada.");
            }
        }

        // Adds the registered PRs right away and registers the rest from Bitbucket in a job. True when a job was started.
        bool AddMembers(long groupId, IReadOnlyList<PrRef> refs)
        {
            var config = context.LoadConfig();
            List<PrRef> missing;
            using (var store = context.OpenStore())
            {
                var known = refs.Select(r => (Ref: r, Pr: store.FindPullRequest(r))).ToList();
                store.AddGroupMembers(groupId, known.Where(k => k.Pr is not null).Select(k => k.Pr!.Id));
                context.Exporter(config)?.ExportGroup(store, groupId);
                missing = known.Where(k => k.Pr is null).Select(k => k.Ref).ToList();
            }
            if (missing.Count == 0)
                return false;

            jobs.Start(GroupJobs.Sync, GroupJobs.Ref(groupId), async job =>
            {
                using var store = context.OpenStore();
                foreach (var prRef in missing)
                {
                    job.Add($"{prRef.Key}: consultando Bitbucket…");
                    var (pr, _) = await new PullRequestSync(store, context.Bitbucket(config), job).RefreshAsync(prRef);
                    store.AddGroupMembers(groupId, [pr.Id]);
                    job.Add($"{pr.Key}: {pr.Title} · agregado al grupo.");
                }
                if (context.Exporter(config) is { } exporter)
                {
                    foreach (var pr in store.ListGroupMembers(groupId))
                        exporter.Export(store, pr.Id);
                    exporter.ExportGroup(store, groupId);
                }
            });
            return true;
        }

        async Task AnalyzeGroupAsync(Job job, long groupId, PanelOptions panelOptions)
        {
            var config = context.LoadConfig();
            using var store = context.OpenStore();
            var panel = new GroupPanelService(config, store, context.Bitbucket(config), name => context.Agent(config, name),
                context.Exporter(config), job, context.RunsRoot, context.Clock, context.LoadProfile, context.GlobalProfile, context.Checkouts(config));
            var outcome = await panel.AnalyzeAsync(groupId, panelOptions);
            job.Add($"Veredicto del grupo: {(outcome.Verdict is { } v ? ReviewStandard.Label(v) : "sin reportes válidos")}");
            foreach (var (key, verdict) in outcome.PrVerdicts)
                job.Add($"{key}: {(verdict is { } pv ? ReviewStandard.Label(pv) : "sin reportes válidos")}");
        }

        async Task AnalyzeAsync(Job job, PanelOptions panelOptions)
        {
            var config = context.LoadConfig();
            using var store = context.OpenStore();
            var panel = new PanelService(config, store, context.Bitbucket(config), name => context.Agent(config, name),
                context.Exporter(config), job, context.RunsRoot, context.Clock, context.LoadProfile, context.Checkouts(config));
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

    /// <summary>PR references separated by spaces, commas or lines; false with the first invalid one.</summary>
    private static bool TryParseRefs(string? text, out List<PrRef> refs, out string? invalid)
    {
        refs = [];
        invalid = null;
        foreach (var part in (text ?? "").Split([' ', ',', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!PrRef.TryParse(part, out var prRef))
            {
                invalid = part;
                return false;
            }
            if (!refs.Contains(prRef))
                refs.Add(prRef);
        }
        return true;
    }

    /// <summary>Reviewers picked in the form; null when none, which runs them all.</summary>
    private static List<string>? Reviewers(IFormCollection form) =>
        form["reviewer"].Where(r => !string.IsNullOrEmpty(r)).Select(r => r!).ToList() is { Count: > 0 } names ? names : null;

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
