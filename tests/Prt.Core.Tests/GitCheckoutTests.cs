using Prt.Core.Config;

namespace Prt.Core.Tests;

public class GitCheckoutTests
{
    private static string Repo(TempDir dir, string name, string remoteUrl)
    {
        var root = Path.Combine(dir.Path, name);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Module"));
        File.WriteAllText(Path.Combine(root, ".git", "config"),
            $"[core]\n\tbare = false\n[remote \"origin\"]\n\turl = {remoteUrl}\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");
        return root;
    }

    [Theory]
    [InlineData("https://ana@bitbucket.org/devteambizlogit/bizion-equus.git")]
    [InlineData("git@bitbucket.org:devteambizlogit/bizion-equus.git")]
    [InlineData("https://bitbucket.org/DevTeamBizlogit/Bizion-Equus")]
    public void Finds_the_checkout_from_any_subfolder(string url)
    {
        using var dir = new TempDir();
        var root = Repo(dir, "equus", url);

        Assert.Equal(root, GitCheckout.FindFor(Path.Combine(root, "src", "Module"), "devteambizlogit/bizion-equus"));
    }

    [Fact]
    public void Ignores_checkouts_of_other_repositories()
    {
        using var dir = new TempDir();
        var root = Repo(dir, "other", "git@bitbucket.org:devteambizlogit/bizion-equus-legacy.git");

        Assert.Equal(root, GitCheckout.FindRoot(root));
        Assert.Null(GitCheckout.FindFor(root, "devteambizlogit/bizion-equus"));
        Assert.Null(GitCheckout.FindFor(dir.Path, "devteambizlogit/bizion-equus"));
    }

    [Fact]
    public void Follows_worktree_git_files_to_the_shared_config()
    {
        using var dir = new TempDir();
        var main = Repo(dir, "main", "git@bitbucket.org:acme/api.git");
        var worktreeGitDir = Path.Combine(main, ".git", "worktrees", "feature");
        Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..\n");
        var worktree = Path.Combine(dir.Path, "feature");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}\n");

        Assert.Equal(worktree, GitCheckout.FindFor(worktree, "acme/api"));
    }
}
