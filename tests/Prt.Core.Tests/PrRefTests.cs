using Prt.Core.Domain;

namespace Prt.Core.Tests;

public class PrRefTests
{
    [Theory]
    [InlineData("https://bitbucket.org/acme/payments/pull-requests/42")]
    [InlineData("https://bitbucket.org/acme/payments/pull-requests/42/diff")]
    [InlineData("https://bitbucket.org/acme/payments/pull-requests/42/overview?w=1")]
    [InlineData("  acme/payments#42 ")]
    [InlineData("acme/payments/42")]
    public void Parses_supported_formats(string text)
    {
        Assert.True(PrRef.TryParse(text, out var prRef));
        Assert.Equal(new PrRef("acme", "payments", 42), prRef);
        Assert.Equal("acme/payments#42", prRef.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("acme/payments")]
    [InlineData("https://github.com/acme/payments/pull/42")]
    [InlineData("acme/payments#0")]
    public void Rejects_invalid_input(string text) => Assert.False(PrRef.TryParse(text, out _));

    [Fact]
    public void Builds_web_url() =>
        Assert.Equal("https://bitbucket.org/acme/payments/pull-requests/7", new PrRef("acme", "payments", 7).WebUrl);
}
