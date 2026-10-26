using Prt.Core.Panel;

namespace Prt.Core.Tests;

public class ProfileLoaderTests
{
    [Fact]
    public void Built_in_profile_has_the_four_standard_reviewers()
    {
        var profile = ProfileLoader.Load([]);

        Assert.Equal(["architecture", "bugs", "performance", "security"], profile.Reviewers.Select(r => r.Name));
        Assert.All(profile.Reviewers, r => Assert.Equal(ProfileLoader.BuiltInSource, r.Source));
        Assert.Equal(["ARQ", "BUG", "PERF", "SEC"], profile.Reviewers.Select(r => r.IdPrefix));
        Assert.Contains("## Severidades", profile.Standards);
        Assert.Equal("", profile.Context);
        Assert.Equal(4, profile.MaxParallel);
    }

    [Fact]
    public void Later_layers_override_reviewers_standards_and_settings()
    {
        using var dir = new TempDir();
        var global = Path.Combine(dir.Path, "global");
        var repo = Path.Combine(dir.Path, "repo", ".prt");
        var user = Path.Combine(dir.Path, "user");

        Write(global, "standards.md", "# Estándar de la empresa");
        Write(global, "context.md", "Somos una fintech.");
        Write(repo, "context.md", "Este servicio procesa pagos QR.");
        Write(repo, "reviewers/security.md", "---\ntitle: Seguridad PCI\nagent: codex\nid_prefix: pci\n---\nRevisá PCI DSS.");
        Write(repo, "reviewers/data.md", "---\nname: data\ntitle: Datos\norder: 0\n---\nRevisá migraciones.");
        Write(repo, "review.json", """{ "defaultAgent": "opencode", "maxParallel": 2, "contextFiles": ["docs/ADR.md"] }""");
        Write(user, "review.json", """{ "agents": { "bugs": "claude" }, "maxParallel": 3 }""");

        var profile = ProfileLoader.Load([global, repo, user, Path.Combine(dir.Path, "missing")]);

        Assert.Equal("data", profile.Reviewers[0].Name);
        var security = profile.Reviewers.Single(r => r.Name == "security");
        Assert.Equal(("Seguridad PCI", "codex", "PCI", "Revisá PCI DSS."), (security.Title, security.Agent, security.IdPrefix, security.Instructions));
        Assert.EndsWith("security.md", security.Source);
        Assert.Equal("claude", profile.Reviewers.Single(r => r.Name == "bugs").Agent);
        Assert.Equal("# Estándar de la empresa", profile.Standards);
        Assert.Equal("Somos una fintech.\n\nEste servicio procesa pagos QR.", profile.Context);
        Assert.Equal(("opencode", 3), (profile.DefaultAgent, profile.MaxParallel));
        Assert.Equal(["docs/ADR.md"], profile.ContextFiles);
        Assert.Equal([global, repo, user], profile.Layers);
    }

    [Fact]
    public void Reviewer_list_in_settings_selects_and_orders()
    {
        using var dir = new TempDir();
        Write(dir.Path, "review.json", """{ "reviewers": ["security", "bugs"] }""");

        var profile = ProfileLoader.Load([dir.Path]);

        Assert.Equal(["security", "bugs"], profile.Select(null).Select(r => r.Name));
        Assert.Equal(["performance"], profile.Select(["performance"]).Select(r => r.Name));
        Assert.Throws<ArgumentException>(() => profile.Select(["ux"]));
    }

    [Fact]
    public void Disabled_reviewers_are_skipped_by_default_but_can_be_requested()
    {
        using var dir = new TempDir();
        Write(dir.Path, "reviewers/performance.md", "---\nenabled: false\n---\nfoco");

        var profile = ProfileLoader.Load([dir.Path]);

        Assert.DoesNotContain(profile.Select(null), r => r.Name == "performance");
        Assert.Single(profile.Select(["performance"]));
    }

    [Theory]
    [InlineData("review.json", """{ "defaultAgent": "gemini" }""")]
    [InlineData("review.json", """{ "reviewers": ["nope"] }""")]
    [InlineData("review.json", "{ no es json")]
    [InlineData("reviewers/x.md", "---\nname: x\n---\n")]
    public void Invalid_configuration_is_reported(string file, string content)
    {
        using var dir = new TempDir();
        Write(dir.Path, file, content);
        Assert.Throws<ArgumentException>(() => ProfileLoader.Load([dir.Path]));
    }

    [Fact]
    public void Scaffold_copies_the_defaults_without_overwriting()
    {
        using var dir = new TempDir();

        var first = ProfileLoader.Scaffold(dir.Path, overwrite: false);
        File.WriteAllText(Path.Combine(dir.Path, "standards.md"), "mío");
        var second = ProfileLoader.Scaffold(dir.Path, overwrite: false);

        Assert.Equal(8, first.Count); // standards, context, review.json, the four reviewers and the coordination one
        Assert.Empty(second);
        Assert.Equal("mío", File.ReadAllText(Path.Combine(dir.Path, "standards.md")));
        var profile = ProfileLoader.Load([dir.Path]);
        Assert.Equal(ProfileLoader.Load([]).Reviewers.Select(r => r.Instructions), profile.Reviewers.Select(r => r.Instructions));
        // The scaffolded context template is only a comment, so it adds nothing.
        Assert.Equal("", profile.Context);
    }

    private static void Write(string dir, string relative, string content)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
