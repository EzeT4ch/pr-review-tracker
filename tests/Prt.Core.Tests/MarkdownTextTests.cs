using Prt.Core.Export;

namespace Prt.Core.Tests;

public class MarkdownTextTests
{
    [Theory]
    [InlineData("Usa IRepository<ProductCode> y </div>", @"Usa IRepository\<ProductCode> y \</div>")]
    [InlineData("Usa `IRepository<ProductCode>`", "Usa `IRepository<ProductCode>`")]
    [InlineData("SKU==código cuesta $5 %% oculto", @"SKU\=\=código cuesta \$5 \%\% oculto")]
    [InlineData("ver [[nota]] y #DP-2044, no PR #140", @"ver \[\[nota\]\] y \#DP-2044, no PR #140")]
    [InlineData("## Hallazgos", "## Hallazgos")]
    [InlineData(@"ya escapado \$ y \<T>", @"ya escapado \$ y \<T>")]
    [InlineData("<https://bitbucket.org/x> **negrita** _cursiva_ [link](u)", "<https://bitbucket.org/x> **negrita** _cursiva_ [link](u)")]
    [InlineData("``a ` b<T>`` y c<T>", @"``a ` b<T>`` y c\<T>")]
    public void Escapes_prose_outside_code(string input, string expected) => Assert.Equal(expected, MarkdownText.Prose(input));

    [Fact]
    public void Leaves_fenced_blocks_alone()
    {
        var input = "antes <T>\n```csharp\nList<int> x = a == b ? 1 : 2; // $\n```\ndespués $";
        Assert.Equal("antes \\<T>\n```csharp\nList<int> x = a == b ? 1 : 2; // $\n```\ndespués \\$", MarkdownText.Prose(input));
    }

    [Fact]
    public void Cell_is_one_line_with_pipes_escaped_also_inside_code() =>
        Assert.Equal(@"a \| b `x \|\| y` c\<T>", MarkdownText.Cell("a | b\n`x || y` c<T>"));

    [Theory]
    [InlineData("src/A.cs:12", "`src/A.cs:12`")]
    [InlineData("a`b", "`` a`b ``")]
    public void Code_wraps_with_enough_backticks(string input, string expected) => Assert.Equal(expected, MarkdownText.Code(input));

    [Theory]
    [InlineData("corto", 10, "corto")]
    [InlineData("usa `MergeCodes` en el lote", 12, "usa `MergeC`…")]
    [InlineData("línea uno\nlínea dos", 50, "línea uno línea dos")]
    public void Truncates_without_leaving_code_open(string input, int max, string expected) =>
        Assert.Equal(expected, MarkdownText.Truncate(input, max));
}
