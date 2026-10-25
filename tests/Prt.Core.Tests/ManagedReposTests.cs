using Prt.Core.Config;
using Prt.Core.Domain;
using Prt.Core.Repos;

namespace Prt.Core.Tests;

/// <summary>Real git against a local bare repository standing in for Bitbucket.</summary>
public sealed class ManagedReposTests : IDisposable
{
    private static readonly PrRef Ref = new("acme", "api", 7);
    private static readonly GitCli Git = new([
        new("user.name", "Test"), new("user.email", "test@example.com"), new("init.defaultBranch", "develop"),
        new("commit.gpgsign", "false"), new("protocol.file.allow", "always")]);

    private readonly TempDir _dir = new();
    private readonly string _work;
    private readonly ManagedRepos _repos;
    private readonly FakeUi _ui = new();
    private readonly BitbucketSettings _settings;

    public ManagedReposTests()
    {
        var remoteRoot = Path.Combine(_dir.Path, "remote");
        var bare = Path.Combine(remoteRoot, "acme", "api.git");
        Directory.CreateDirectory(bare);
        Run(bare, "init", "--bare", "--quiet");

        _work = Path.Combine(_dir.Path, "work");
        Directory.CreateDirectory(_work);
        Run(_work, "init", "--quiet");
        Commit("README.md", "v1 de develop");
        Run(_work, "checkout", "--quiet", "-b", "feature/x");
        Commit("src/Feature.cs", "nuevo");
        Run(_work, "checkout", "--quiet", "develop");
        Run(_work, "branch", "main");
        Run(_work, "remote", "add", "origin", bare);
        Run(_work, "push", "--quiet", "origin", "develop", "feature/x", "main");

        _settings = new BitbucketSettings { GitBaseUrl = new Uri(remoteRoot).AbsoluteUri, Username = "ana@acme.com", ApiToken = "secreto" };
        _repos = new ManagedRepos(Path.Combine(_dir.Path, "repos"), _settings);
    }

    public void Dispose() => _dir.Dispose();

    private void Run(string cwd, params string[] args) => Git.RunCheckedAsync(cwd, args).GetAwaiter().GetResult();

    private void Commit(string file, string content)
    {
        var path = Path.Combine(_work, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        Run(_work, "add", ".");
        Run(_work, "commit", "--quiet", "-m", $"cambia {file}");
    }

    [Fact]
    public async Task Prepares_the_destination_branch_and_fetches_the_source()
    {
        var info = await _repos.PrepareAsync(Ref, "develop", "feature/x", _ui);

        Assert.Equal(CheckoutKind.Managed, info.Kind);
        Assert.Equal("develop", info.BaseBranch);
        Assert.Equal("v1 de develop", File.ReadAllText(Path.Combine(info.Path, "README.md")));
        Assert.False(File.Exists(Path.Combine(info.Path, "src", "Feature.cs")));
        var diff = await Git.RunCheckedAsync(info.Path, ["diff", "--name-only", "origin/develop...origin/feature/x"]);
        Assert.Equal("src/Feature.cs", diff);
        Assert.Contains(_ui.Messages, m => m.Contains("Clonando acme/api"));

        // Credentials travel in the environment, never in the clone's config.
        var config = File.ReadAllText(Path.Combine(_repos.RepoDir("acme/api"), "main", ".git", "config"));
        Assert.DoesNotContain("Authorization", config);
        Assert.DoesNotContain("secreto", config);
    }

    [Fact]
    public async Task Later_preparations_follow_the_branch_and_discard_local_changes()
    {
        var first = await _repos.PrepareAsync(Ref, "develop", null, _ui);
        File.WriteAllText(Path.Combine(first.Path, "README.md"), "tocado por un agente");
        File.WriteAllText(Path.Combine(first.Path, "basura.txt"), "x");
        Commit("README.md", "v2 de develop");
        Run(_work, "push", "--quiet", "origin", "develop");

        var second = await _repos.PrepareAsync(Ref, "develop", null, _ui);

        Assert.Equal(first.Path, second.Path);
        Assert.NotEqual(first.BaseCommit, second.BaseCommit);
        Assert.Equal("v2 de develop", File.ReadAllText(Path.Combine(second.Path, "README.md")));
        Assert.False(File.Exists(Path.Combine(second.Path, "basura.txt")));
        Assert.Single(_ui.Messages, m => m.Contains("Clonando"));
    }

    [Fact]
    public async Task Each_destination_branch_gets_its_own_worktree()
    {
        var develop = await _repos.PrepareAsync(Ref, "develop", null, _ui);
        var main = await _repos.PrepareAsync(Ref, "main", "feature/gone", _ui);

        Assert.NotEqual(develop.Path, main.Path);
        Assert.Equal(["develop", "main"], _repos.Worktrees("acme/api"));
        Assert.Contains(_ui.Messages, m => m.StartsWith("warn: No se pudo traer la rama feature/gone"));
        Assert.True(_repos.SizeBytes("acme/api") > 0);

        _repos.Delete("acme/api");
        Assert.False(_repos.Exists("acme/api"));
    }

    [Fact]
    public async Task Resolver_prefers_the_local_checkout_and_falls_back_to_the_diff()
    {
        var pr = new PullRequest { Workspace = "acme", Repo = "api", Number = 7, DestinationBranch = "develop", SourceBranch = "feature/x" };
        var local = Directory.CreateDirectory(Path.Combine(_dir.Path, "mine")).FullName;
        var config = new PrtConfig { Bitbucket = _settings };

        config.RepoPaths["acme/api"] = local;
        Assert.Equal(new CheckoutInfo(local, CheckoutKind.Local), await new CheckoutResolver(config, _repos).ResolveAsync(pr, _ui));
        Assert.False(_repos.Exists("acme/api"));

        config.RepoPaths.Clear();
        Assert.Equal(CheckoutKind.Managed, (await new CheckoutResolver(config, _repos).ResolveAsync(pr, _ui))!.Kind);

        config.ManagedClones = false;
        Assert.Null(await new CheckoutResolver(config, _repos).ResolveAsync(pr, _ui));

        config.ManagedClones = true;
        var broken = new ManagedRepos(Path.Combine(_dir.Path, "other"), new BitbucketSettings { GitBaseUrl = new Uri(Path.Combine(_dir.Path, "nada")).AbsoluteUri });
        Assert.Null(await new CheckoutResolver(config, broken).ResolveAsync(pr, _ui));
        Assert.Contains(_ui.Messages, m => m.Contains("No se pudo preparar el clon gestionado"));
    }

    [Fact]
    public void Git_username_depends_on_the_kind_of_credential()
    {
        Assert.Equal("x-bitbucket-api-token-auth", new BitbucketSettings { Username = "ana@acme.com" }.EffectiveGitUsername);
        Assert.Equal("ana", new BitbucketSettings { Username = "ana" }.EffectiveGitUsername);
        Assert.Equal("bot", new BitbucketSettings { Username = "ana@acme.com", GitUsername = "bot" }.EffectiveGitUsername);
    }
}
