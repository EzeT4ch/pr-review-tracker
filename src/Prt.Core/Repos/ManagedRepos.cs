using System.Text.RegularExpressions;
using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Review;

namespace Prt.Core.Repos;

public enum CheckoutKind
{
    /// <summary>A checkout of the user, mapped with `prt map`.</summary>
    Local,

    /// <summary>prt's own clone, at the tip of the PR's destination branch.</summary>
    Managed,
}

/// <summary>Where agents read the code from. <see cref="BaseBranch"/>/<see cref="BaseCommit"/> are set for managed clones.</summary>
public sealed record CheckoutInfo(string Path, CheckoutKind Kind, string? BaseBranch = null, string? BaseCommit = null)
{
    public string Describe() => Kind == CheckoutKind.Local
        ? $"checkout local {Path}"
        : $"clon gestionado en {BaseBranch} @ {Bitbucket.CommitHashes.Short(BaseCommit)}";
}

/// <summary>
/// Read-only clones kept by prt under the data folder: one partial clone per repo
/// (<c>repos/&lt;ws&gt;/&lt;repo&gt;/main</c>) and one worktree per destination branch
/// (<c>repos/&lt;ws&gt;/&lt;repo&gt;/worktrees/&lt;branch&gt;</c>), so agents see the project as it is today.
/// </summary>
public sealed partial class ManagedRepos(string root, BitbucketSettings settings)
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(15);

    public string Root => root;

    public string RepoDir(string repoKey) => Path.Combine(root, Path.Combine(repoKey.Split('/').Select(SafeSegment).ToArray()));

    public string WorktreeDir(string repoKey, string branch) => Path.Combine(RepoDir(repoKey), "worktrees", SafeSegment(branch));

    public bool Exists(string repoKey) => Directory.Exists(Path.Combine(RepoDir(repoKey), "main", ".git"));

    /// <summary>Destination branches with a prepared worktree.</summary>
    public IReadOnlyList<string> Worktrees(string repoKey)
    {
        var dir = Path.Combine(RepoDir(repoKey), "worktrees");
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().Order().ToList() : [];
    }

    /// <summary>Any prepared worktree of the repo (to read its committed .prt/ settings), or null.</summary>
    public string? AnyWorktree(string repoKey) =>
        Worktrees(repoKey).Select(name => Path.Combine(RepoDir(repoKey), "worktrees", name)).FirstOrDefault();

    public long SizeBytes(string repoKey)
    {
        var dir = RepoDir(repoKey);
        if (!Directory.Exists(dir))
            return 0;
        return new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
            .Sum(f => f.Length);
    }

    public string Size(string repoKey)
    {
        var bytes = SizeBytes(repoKey);
        return bytes < 1024 * 1024
            ? $"{Math.Max(1, bytes / 1024)} KB"
            : $"{Math.Round(bytes / 1024d / 1024d, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)} MB";
    }

    public void Delete(string repoKey) => ForceDelete(RepoDir(repoKey));

    private static void ForceDelete(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        // Git marks pack files read-only, which blocks deletion on Windows.
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>Clones or updates the repo and leaves a worktree at the tip of <paramref name="baseBranch"/>.</summary>
    public async Task<CheckoutInfo> PrepareAsync(PrRef prRef, string baseBranch, string? sourceBranch, IStatusUi ui, CancellationToken ct = default)
    {
        var repoDir = RepoDir(prRef.RepoKey);
        Directory.CreateDirectory(repoDir);
        using var repoLock = await AcquireLockAsync(Path.Combine(repoDir, ".lock"), ui, ct);

        var git = GitCli.WithBasicAuth(settings.GitBaseUrl, settings.EffectiveGitUsername, settings.EffectiveToken);
        var url = $"{settings.GitBaseUrl.TrimEnd('/')}/{prRef.Workspace}/{prRef.Repo}.git";
        var main = Path.Combine(repoDir, "main");

        if (!Directory.Exists(Path.Combine(main, ".git")))
        {
            ui.Info($"Clonando {prRef.RepoKey} (solo la primera vez; descarga el historial sin el contenido de los archivos)…");
            ForceDelete(main);
            try
            {
                await git.RunCheckedAsync(repoDir, ["clone", "--filter=blob:none", "--no-checkout", "--quiet", url, "main"], ct);
            }
            catch
            {
                ForceDelete(main);
                throw;
            }
        }
        else
        {
            await git.RunCheckedAsync(main, ["remote", "set-url", "origin", url], ct);
        }

        ui.Info($"Actualizando {baseBranch} desde Bitbucket…");
        await git.RunCheckedAsync(main, ["fetch", "--quiet", "origin", baseBranch], ct);
        if (!string.IsNullOrEmpty(sourceBranch) && sourceBranch != baseBranch)
        {
            // The source branch may be gone (merged and deleted): not needed to read the base.
            var source = await git.RunAsync(main, ["fetch", "--quiet", "origin", sourceBranch], ct);
            if (source.ExitCode != 0)
                ui.Warn($"No se pudo traer la rama {sourceBranch}: {source.StdErr.Trim()}");
        }

        var worktree = WorktreeDir(prRef.RepoKey, baseBranch);
        var target = $"origin/{baseBranch}";
        if (Directory.Exists(worktree) && File.Exists(Path.Combine(worktree, ".git")))
        {
            await git.RunCheckedAsync(worktree, ["checkout", "--quiet", "--force", "--detach", target], ct);
            await git.RunCheckedAsync(worktree, ["clean", "-fdq"], ct);
        }
        else
        {
            ForceDelete(worktree);
            await git.RunCheckedAsync(main, ["worktree", "prune"], ct);
            Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
            await git.RunCheckedAsync(main, ["worktree", "add", "--quiet", "--force", "--detach", worktree, target], ct);
        }

        var commit = await git.RunCheckedAsync(worktree, ["rev-parse", "HEAD"], ct);
        return new CheckoutInfo(worktree, CheckoutKind.Managed, baseBranch, commit);
    }

    private static async Task<FileStream> AcquireLockAsync(string path, IStatusUi ui, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        var told = false;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                if (!told)
                {
                    ui.Info("Esperando a que termine otra actualización del mismo repo…");
                    told = true;
                }
                await Task.Delay(500, ct);
            }
        }
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex Unsafe();

    private static string SafeSegment(string value) => Unsafe().Replace(value, "-");
}

public interface ICheckoutProvider
{
    /// <summary>The code agents should read for the PR, or null when only the diff is available.</summary>
    Task<CheckoutInfo?> ResolveAsync(PullRequest pr, IStatusUi ui, CancellationToken ct = default);
}

/// <summary>Local checkout mapped by the user first, then prt's managed clone, then nothing (diff only).</summary>
public sealed class CheckoutResolver(PrtConfig config, ManagedRepos managed) : ICheckoutProvider
{
    public async Task<CheckoutInfo?> ResolveAsync(PullRequest pr, IStatusUi ui, CancellationToken ct = default)
    {
        var repoKey = pr.Ref.RepoKey;
        if (config.RepoPathFor(repoKey) is { } local)
            return new CheckoutInfo(local, CheckoutKind.Local);

        if (!config.ManagedClones)
        {
            ui.Warn($"{repoKey} no tiene checkout local asociado (`prt map {repoKey} <ruta>`): los revisores solo verán el diff.");
            return null;
        }
        if (string.IsNullOrEmpty(pr.DestinationBranch))
        {
            ui.Warn("No se conoce la rama de destino del PR: los revisores solo verán el diff.");
            return null;
        }

        try
        {
            var info = await managed.PrepareAsync(pr.Ref, pr.DestinationBranch, pr.SourceBranch, ui, ct);
            ui.Info($"Código: {info.Describe()}.");
            return info;
        }
        catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException)
        {
            ui.Warn($"No se pudo preparar el clon gestionado ({ex.Message}). Los revisores solo verán el diff.");
            return null;
        }
    }
}

/// <summary>Only checkouts mapped by the user (the behaviour before managed clones; used in tests).</summary>
public sealed class LocalCheckouts(PrtConfig config) : ICheckoutProvider
{
    public Task<CheckoutInfo?> ResolveAsync(PullRequest pr, IStatusUi ui, CancellationToken ct = default)
    {
        if (config.RepoPathFor(pr.Ref.RepoKey) is { } local)
            return Task.FromResult<CheckoutInfo?>(new CheckoutInfo(local, CheckoutKind.Local));
        ui.Warn($"{pr.Ref.RepoKey} no tiene checkout local asociado (`prt map {pr.Ref.RepoKey} <ruta>`): los revisores solo verán el diff.");
        return Task.FromResult<CheckoutInfo?>(null);
    }
}
