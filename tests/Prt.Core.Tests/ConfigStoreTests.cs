using Prt.Core.Config;

namespace Prt.Core.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Saves_only_the_prompts_that_differ_from_the_built_in_ones()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        var config = new PrtConfig();
        config.Prompts.Initial = "Mi prompt";

        ConfigStore.Save(config, path);

        var text = File.ReadAllText(path);
        Assert.Contains("\"initial\": \"Mi prompt\"", text);
        Assert.DoesNotContain("\"summary\"", text);
        var loaded = ConfigStore.Load(path);
        Assert.Equal("Mi prompt", loaded.Prompts.Initial);
        Assert.Equal(DefaultPrompts.Summary, loaded.Prompts.Summary);
    }

    [Fact]
    public void Omits_the_prompts_section_when_nothing_was_customized()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");

        ConfigStore.Save(new PrtConfig(), path);

        Assert.DoesNotContain("\"prompts\"", File.ReadAllText(path));
    }
}
