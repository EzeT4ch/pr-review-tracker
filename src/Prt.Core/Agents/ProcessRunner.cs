using System.Diagnostics;

namespace Prt.Core.Agents;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public interface IProcessRunner
{
    /// <summary>Runs attached to the current terminal (the user interacts with the agent).</summary>
    Task<int> RunInteractiveAsync(string command, IReadOnlyList<string> args, string cwd, CancellationToken ct = default);

    /// <summary>Runs headless with stdin closed and output captured.</summary>
    Task<ProcessResult> RunCapturedAsync(string command, IReadOnlyList<string> args, string cwd, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunInteractiveAsync(string command, IReadOnlyList<string> args, string cwd, CancellationToken ct = default)
    {
        var info = CreateStartInfo(command, args, cwd);
        using var process = Start(info, command);
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    public async Task<ProcessResult> RunCapturedAsync(string command, IReadOnlyList<string> args, string cwd, CancellationToken ct = default)
    {
        var info = CreateStartInfo(command, args, cwd);
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var process = Start(info, command);
        // Agents in print mode may wait for piped input otherwise.
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static Process Start(ProcessStartInfo info, string command)
    {
        try
        {
            return Process.Start(info) ?? throw new InvalidOperationException($"No se pudo iniciar '{command}'.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"No se pudo ejecutar '{command}': {ex.Message}. ¿Está instalado y en el PATH? " +
                "Se puede configurar la ruta en agents.<agente>.command del config.", ex);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string command, IReadOnlyList<string> args, string cwd)
    {
        var resolved = CommandResolver.Resolve(command);
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows() && resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            OperatingSystem.IsWindows() && resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // npm shims are batch files; cmd.exe cannot carry newlines inside an argument.
            info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/s");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(resolved);
            foreach (var arg in args)
                info.ArgumentList.Add(arg.ReplaceLineEndings(" "));
        }
        else
        {
            info = new ProcessStartInfo(resolved) { UseShellExecute = false };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
        }
        info.WorkingDirectory = cwd;
        return info;
    }
}

internal static class CommandResolver
{
    /// <summary>Resolves a bare command through PATH (and PATHEXT on Windows).</summary>
    public static string Resolve(string command)
    {
        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains('/'))
            return command;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        if (OperatingSystem.IsWindows() && Path.HasExtension(command))
            extensions = ["", .. extensions];

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, command + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return command;
    }
}
