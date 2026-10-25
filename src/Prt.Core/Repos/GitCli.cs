using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Prt.Core.Agents;

namespace Prt.Core.Repos;

public sealed class GitException(string message) : Exception(message);

/// <summary>
/// Runs git without a console. Configuration (credentials included) goes through GIT_CONFIG_* environment
/// variables, so it is never written to .git/config nor visible in the process list.
/// </summary>
public sealed class GitCli
{
    private readonly IReadOnlyList<KeyValuePair<string, string>> _config;

    public GitCli(IEnumerable<KeyValuePair<string, string>>? config = null)
    {
        var all = new List<KeyValuePair<string, string>>(config ?? []);
        if (OperatingSystem.IsWindows())
            all.Add(new("core.longpaths", "true"));
        _config = all;
    }

    /// <summary>Git with HTTP Basic credentials sent only to <paramref name="baseUrl"/>.</summary>
    public static GitCli WithBasicAuth(string baseUrl, string? username, string? token)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(token))
            return new GitCli();
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
        return new GitCli([new($"http.{baseUrl.TrimEnd('/')}/.extraHeader", $"Authorization: Basic {basic}")]);
    }

    public async Task<ProcessResult> RunAsync(string cwd, IEnumerable<string> args, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        // Never wait for a password prompt or a credential manager window.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "never";
        info.Environment["GIT_CONFIG_COUNT"] = _config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < _config.Count; i++)
        {
            info.Environment[$"GIT_CONFIG_KEY_{i}"] = _config[i].Key;
            info.Environment[$"GIT_CONFIG_VALUE_{i}"] = _config[i].Value;
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new GitException("No se pudo iniciar git.");
        }
        catch (Win32Exception)
        {
            throw new GitException("git no está instalado o no está en el PATH: hace falta para el clon gestionado.");
        }

        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
    }

    /// <summary>Runs git and returns its trimmed output, or throws with git's message.</summary>
    public async Task<string> RunCheckedAsync(string cwd, IEnumerable<string> args, CancellationToken ct = default)
    {
        var list = args.ToList();
        var result = await RunAsync(cwd, list, ct);
        if (result.ExitCode != 0)
        {
            var detail = (string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr).Trim();
            throw new GitException($"git {list.FirstOrDefault()} falló: {Hint(detail)}");
        }
        return result.StdOut.Trim();
    }

    private static string Hint(string detail)
    {
        if (detail.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("403", StringComparison.Ordinal))
            detail += " — revisá que el API token tenga permiso de lectura de repositorios (y bitbucket.gitUsername en el config si usás otro usuario).";
        return detail.Length > 600 ? detail[..600] + "…" : detail;
    }
}
