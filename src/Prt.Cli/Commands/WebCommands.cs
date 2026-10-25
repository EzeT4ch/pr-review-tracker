using System.CommandLine;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Prt.Core.Agents;
using Prt.Core.Bitbucket;
using Prt.Core.Config;
using Microsoft.Extensions.Hosting;
using Prt.Web;
using Spectre.Console;

namespace Prt.Cli.Commands;

internal static class WebCommands
{
    private const int DefaultPort = 5178;
    private const int DefaultSleepMinutes = 10;

    public static Command Web()
    {
        var command = new Command("web", "Panel web local (http://127.0.0.1) para consultar PRs, análisis y registrar memos");
        command.Subcommands.Add(Start());
        command.Subcommands.Add(Stop());
        command.Subcommands.Add(Status());
        command.Subcommands.Add(Serve());
        return command;
    }

    private static Option<int> PortOption() => new("--port") { Description = "Puerto local", DefaultValueFactory = _ => DefaultPort };

    private static Option<int> SleepOption() => new("--sleep-after")
    {
        Description = "Minutos sin uso tras los que el servidor entra en reposo y libera memoria (0: nunca)",
        DefaultValueFactory = _ => DefaultSleepMinutes,
    };

    private static Command Start()
    {
        var port = PortOption();
        var sleep = SleepOption();
        var open = new Option<bool>("--open") { Description = "Abrir el navegador" };
        var command = new Command("start", "Inicia el panel web en segundo plano") { port, sleep, open };
        command.SetAction((result, ct) => Cli.Run(async app =>
        {
            if (await WebState.FindRunningAsync(ct) is { } running)
            {
                app.Ui.Info($"El panel ya está corriendo en {running.Url} (pid {running.Pid}).");
                if (result.GetValue(open))
                    OpenBrowser(running.Url);
                return;
            }

            var started = Process.Start(SelfStartInfo(["web", "serve", "--background",
                "--port", result.GetValue(port).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--sleep-after", result.GetValue(sleep).ToString(System.Globalization.CultureInfo.InvariantCulture)]))
                ?? throw new InvalidOperationException("No se pudo iniciar el servidor.");

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !started.HasExited)
            {
                if (await WebState.FindRunningAsync(ct) is { } state && state.Pid == started.Id)
                {
                    app.Ui.Success($"Panel web en {state.Url} (pid {state.Pid}). Apagalo con `prt web stop`.");
                    if (result.GetValue(open))
                        OpenBrowser(state.Url);
                    return;
                }
                await Task.Delay(200, ct);
            }

            throw new InvalidOperationException(started.HasExited
                ? $"El servidor terminó al iniciar (¿el puerto {result.GetValue(port)} está ocupado?). Detalle en {WebState.LogPath}."
                : $"El servidor no respondió a tiempo. Detalle en {WebState.LogPath}.");
        }));
        return command;
    }

    private static Command Stop()
    {
        var command = new Command("stop", "Apaga el panel web");
        command.SetAction((_, ct) => Cli.Run(async app =>
        {
            var state = WebState.Read();
            if (state is null)
            {
                app.Ui.Info("El panel web no está corriendo.");
                return;
            }

            var process = state.Process();
            try
            {
                using var http = WebState.Client();
                using var request = new HttpRequestMessage(HttpMethod.Post, state.Url + "/api/shutdown");
                request.Headers.Add(PrtWebServer.TokenHeader, state.Token);
                (await http.SendAsync(request, ct)).EnsureSuccessStatusCode();
            }
            catch (HttpRequestException)
            {
                // Not answering: the process is stopped by force below.
            }

            if (process is not null && !await WaitForExitAsync(process, TimeSpan.FromSeconds(10), ct))
            {
                process.Kill(entireProcessTree: true);
                await WaitForExitAsync(process, TimeSpan.FromSeconds(5), ct);
            }
            WebState.Delete();
            app.Ui.Success("Panel web apagado.");
        }));
        return command;
    }

    private static Command Status()
    {
        var command = new Command("status", "Muestra si el panel web está corriendo, su memoria y si está en reposo");
        command.SetAction((_, ct) => Cli.Run(async app =>
        {
            var state = WebState.Read();
            if (state is null)
            {
                app.Ui.Info("El panel web está apagado. Inicialo con `prt web start`.");
                return;
            }

            var health = await WebState.HealthAsync(state, ct);
            if (health is null)
            {
                app.Ui.Warn(state.Process() is null
                    ? "El panel web terminó sin apagarse limpiamente; se limpia su estado."
                    : $"El proceso {state.Pid} existe pero no responde en {state.Url}. Probá `prt web stop`.");
                if (state.Process() is null)
                    WebState.Delete();
                return;
            }

            var h = health.Value;
            var table = new Table().Border(Symbols.Border).HideHeaders().AddColumns("", "");
            table.AddRow("URL", Markup.Escape(state.Url));
            table.AddRow("PID", state.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var sleeps = h.GetProperty("sleeps").GetInt32();
            table.AddRow("Estado", sleeps == 0 ? "activo" : $"activo (entró en reposo {sleeps} {(sleeps == 1 ? "vez" : "veces")})");
            table.AddRow("Memoria", $"{h.GetProperty("privateMb").GetDouble()} MB propia · {h.GetProperty("workingSetMb").GetDouble()} MB contando librerías compartidas");
            table.AddRow("Activo desde", Markup.Escape(state.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
            table.AddRow("Tareas en curso", h.GetProperty("runningJobs").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
            AnsiConsole.Write(table);
        }));
        return command;
    }

    private static Command Serve()
    {
        var port = PortOption();
        var sleep = SleepOption();
        var background = new Option<bool>("--background") { Hidden = true };
        var command = new Command("serve", "Corre el panel web en primer plano (Ctrl+C para salir)") { port, sleep, background };
        command.SetAction((result, ct) => Cli.Run(async app =>
        {
            var isBackground = result.GetValue(background);
            if (await WebState.FindRunningAsync(ct) is { } running)
                throw new UserException($"El panel ya está corriendo en {running.Url} (pid {running.Pid}).");

            // A background server outlives the terminal that started it.
            using var hangup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => c.Cancel = true);

            var token = RandomNumberGenerator.GetHexString(32, lowercase: true);
            var context = Context();
            var server = PrtWebServer.Build(context, new WebOptions(result.GetValue(port), TimeSpan.FromMinutes(Math.Max(0, result.GetValue(sleep))), token));
            try
            {
                await server.StartAsync(ct);
            }
            catch (IOException ex)
            {
                Log(isBackground, $"No se pudo abrir el puerto {result.GetValue(port)}: {ex.Message}");
                throw new UserException($"No se pudo abrir el puerto {result.GetValue(port)} (¿está ocupado? usá --port).");
            }

            var state = new WebState(Environment.ProcessId, PrtWebServer.Port(server), token, DateTimeOffset.Now);
            state.Write();
            Log(isBackground, $"Panel web en {state.Url} (pid {state.Pid}).");
            if (!isBackground)
                app.Ui.Success($"Panel web en {state.Url}. Ctrl+C para salir.");
            try
            {
                await server.WaitForShutdownAsync(ct);
            }
            finally
            {
                if (WebState.Read()?.Pid == Environment.ProcessId)
                    WebState.Delete();
                Log(isBackground, "Panel web detenido.");
                await server.DisposeAsync();
            }
        }));
        return command;
    }

    private static readonly HttpClient BitbucketHttp =
        new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };

    private static WebContext Context() => new()
    {
        LoadConfig = () => ConfigStore.Load(),
        DatabasePath = PrtPaths.DatabaseFile,
        RunsRoot = Path.Combine(PrtPaths.DataDir, "runs"),
        Bitbucket = config => new BitbucketClient(BitbucketHttp, config.Bitbucket),
        Agent = (config, name) => AgentFactory.Create(name, config, new ProcessRunner()),
    };

    /// <summary>Command line that runs this same prt, whether launched as an app host or through `dotnet prt.dll`.</summary>
    private static ProcessStartInfo SelfStartInfo(IEnumerable<string> args)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No se pudo ubicar el ejecutable de prt.");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = PrtPaths.DataDir,
        };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(WebCommands).Assembly.Location);
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        Directory.CreateDirectory(PrtPaths.DataDir);
        return info;
    }

    private static void Log(bool background, string line)
    {
        if (!background)
            return;
        try
        {
            File.AppendAllText(WebState.LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging is best effort.
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            AnsiConsole.MarkupLine($"{Symbols.Warning} No se pudo abrir el navegador; entrá a {Markup.Escape(url)}");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}

/// <summary>web.json in the data folder: how to reach (and stop) the running server.</summary>
internal sealed record WebState(int Pid, int Port, string Token, DateTimeOffset StartedAt)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string Path => System.IO.Path.Combine(PrtPaths.DataDir, "web.json");
    public static string LogPath => System.IO.Path.Combine(PrtPaths.DataDir, "web.log");

    public string Url => $"http://127.0.0.1:{Port}";

    public static WebState? Read()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<WebState>(File.ReadAllText(Path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public void Write()
    {
        Directory.CreateDirectory(PrtPaths.DataDir);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
        // Holds the shutdown token.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void Delete() => File.Delete(Path);

    /// <summary>The server process, or null when it no longer exists (or the pid was reused).</summary>
    public Process? Process()
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(Pid);
            return process.HasExited ? null : process;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static HttpClient Client() => new() { Timeout = TimeSpan.FromSeconds(3) };

    public static async Task<JsonElement?> HealthAsync(WebState state, CancellationToken ct)
    {
        try
        {
            using var http = Client();
            return await http.GetFromJsonAsync<JsonElement>(state.Url + "/health", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The running server, cleaning up a stale state file.</summary>
    public static async Task<WebState?> FindRunningAsync(CancellationToken ct)
    {
        if (Read() is not { } state)
            return null;
        if (await HealthAsync(state, ct) is { } health && health.GetProperty("pid").GetInt32() == state.Pid)
            return state;
        if (state.Process() is null)
            Delete();
        return null;
    }
}
