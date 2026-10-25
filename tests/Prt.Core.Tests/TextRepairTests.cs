using Prt.Core.Export;

namespace Prt.Core.Tests;

public class TextRepairTests
{
    [Theory]
    [InlineData("Revis├® el PR", "Revisé el PR")]
    [InlineData("┬┐Qu├® se hace?", "¿Qué se hace?")]
    [InlineData("el test `ÔÇªReactivatesIt`", "el test `…ReactivatesIt`")]
    [InlineData("c├│digos f├¡sicamente, sem├íntica, ├▒", "códigos físicamente, semántica, ñ")]
    [InlineData("­ƒö┤ bloqueante", "🔴 bloqueante")]
    public void Repairs_utf8_read_as_cp850(string garbled, string expected) => Assert.Equal(expected, TextRepair.Fix(garbled));

    [Theory]
    [InlineData("Revisé el PR: ¿qué pasa con los códigos? Ñandú…")]
    [InlineData("├── src\n│   └── Program.cs")]
    [InlineData("")]
    public void Leaves_correct_text_alone(string text) => Assert.Equal(text, TextRepair.Fix(text));

    [Fact]
    public void Repairs_only_the_garbled_part_of_mixed_text() =>
        Assert.Equal("Revisé y también revisé", TextRepair.Fix("Revis├® y también revisé"));
}
