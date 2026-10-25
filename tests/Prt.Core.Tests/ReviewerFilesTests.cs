using Prt.Core.Panel;

namespace Prt.Core.Tests;

public class ReviewerFilesTests
{
    private static ReviewerDefinition Reviewer(string name = "security", bool enabled = true, string? agent = null) => new()
    {
        Name = name, Title = "Seguridad: PCI", IdPrefix = "SEC", Instructions = "Revisá datos de tarjeta.\n\n- PAN\n- CVV",
        Enabled = enabled, Agent = agent, Order = 7,
    };

    [Fact]
    public void Saved_reviewer_round_trips_and_overrides_the_lower_layers()
    {
        using var dir = new TempDir();
        var global = Path.Combine(dir.Path, "global");
        var user = Path.Combine(dir.Path, "user");

        var path = ReviewerFiles.Save(user, Reviewer(agent: "codex"));

        var loaded = ProfileLoader.Load([global, user]).Reviewers.Single(r => r.Name == "security");
        Assert.Equal(path, loaded.Source);
        Assert.Equal(("Seguridad: PCI", "SEC", "codex", 7, true), (loaded.Title, loaded.IdPrefix, loaded.Agent, loaded.Order, loaded.Enabled));
        Assert.Equal("Revisá datos de tarjeta.\n\n- PAN\n- CVV", loaded.Instructions);
        Assert.Equal(ProfileLayer.User, ProfileLoader.LayerOf(loaded.Source, global, null, user));

        Assert.True(ReviewerFiles.Delete(user, "security"));
        Assert.Equal(ProfileLoader.BuiltInSource, ProfileLoader.Load([global, user]).Reviewers.Single(r => r.Name == "security").Source);
        Assert.False(ReviewerFiles.Delete(user, "security"));
    }

    [Fact]
    public void Editing_keeps_review_json_lists_and_agent_overrides_consistent()
    {
        using var dir = new TempDir();
        var repo = Path.Combine(dir.Path, "repo");
        var user = Path.Combine(dir.Path, "user");
        ReviewerFiles.UpdateSettings(repo, s =>
        {
            s.Reviewers = ["security", "bugs"];
            s.Agents = new() { ["security"] = "codex" };
        });
        var profile = ProfileLoader.Load([repo, user]);

        // Disabling security and changing its agent from the editor, saved in the user layer.
        ReviewerFiles.SaveEdited(user, Reviewer(enabled: false, agent: "opencode"), profile);
        var after = ProfileLoader.Load([repo, user]);
        Assert.Equal(["bugs"], after.Select(null).Select(r => r.Name));
        Assert.Equal("opencode", after.Reviewers.Single(r => r.Name == "security").Agent);

        // Re-enabling appends it to the list; the repo layer is left as it was.
        ReviewerFiles.SaveEdited(user, Reviewer(enabled: true, agent: "opencode"), after);
        Assert.Equal(["bugs", "security"], ProfileLoader.Load([repo, user]).Select(null).Select(r => r.Name));
        Assert.Equal(["security", "bugs"], ProfileLoader.Load([repo]).ExplicitReviewers);
    }

    [Fact]
    public void Without_review_json_only_the_reviewer_file_is_written()
    {
        using var dir = new TempDir();
        ReviewerFiles.SaveEdited(dir.Path, Reviewer(enabled: false), ProfileLoader.Load([dir.Path]));

        Assert.False(File.Exists(Path.Combine(dir.Path, "review.json")));
        Assert.DoesNotContain(ProfileLoader.Load([dir.Path]).Select(null), r => r.Name == "security");
    }

    [Theory]
    [InlineData("Security", "SEC", "x", null, "minúsculas")]
    [InlineData("pci", "p", "x", null, "prefijo")]
    [InlineData("pci", "PCI", "  ", null, "instrucciones")]
    [InlineData("pci", "PCI", "x", "gemini", "Agente desconocido")]
    public void Rejects_invalid_definitions(string name, string prefix, string instructions, string? agent, string message)
    {
        using var dir = new TempDir();
        var bad = Reviewer() with { Name = name, IdPrefix = prefix, Instructions = instructions, Agent = agent };
        var ex = Assert.Throws<ArgumentException>(() => ReviewerFiles.Save(dir.Path, bad));
        Assert.Contains(message, ex.Message);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "reviewers")));
    }

    [Fact]
    public void Layer_of_a_source_prefers_the_most_specific_folder()
    {
        var global = Path.Combine(Path.GetTempPath(), "cfg");
        var user = Path.Combine(global, "projects", "acme", "api");
        var repo = Path.Combine(Path.GetTempPath(), "checkout", ".prt");

        Assert.Equal(ProfileLayer.User, ProfileLoader.LayerOf(Path.Combine(user, "reviewers", "x.md"), global, repo, user));
        Assert.Equal(ProfileLayer.Global, ProfileLoader.LayerOf(Path.Combine(global, "reviewers", "x.md"), global, repo, user));
        Assert.Equal(ProfileLayer.Repo, ProfileLoader.LayerOf(Path.Combine(repo, "reviewers", "x.md"), global, repo, user));
        Assert.Equal(ProfileLayer.BuiltIn, ProfileLoader.LayerOf(ProfileLoader.BuiltInSource, global, repo, user));
    }
}
