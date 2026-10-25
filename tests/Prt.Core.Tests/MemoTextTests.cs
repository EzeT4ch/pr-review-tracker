using Prt.Core.Export;

namespace Prt.Core.Tests;

public class MemoTextTests
{
    private const string Summary =
        """
        ## Resumen
        Agrega cobro QR.

        ## Pendientes
        - [ ] Agregar test de timeout
        - [ ] Validar idempotencia

        ## Otro
        texto

        **Veredicto:** CAMBIOS REQUERIDOS
        """;

    [Theory]
    [InlineData(Summary, "CAMBIOS REQUERIDOS")]
    [InlineData("Veredicto: APROBAR", "APROBAR")]
    [InlineData("texto\nveredicto - `APROBAR CON CAMBIOS`\n", "APROBAR CON CAMBIOS")]
    [InlineData("sin veredicto", null)]
    public void Extracts_verdict(string memo, string? expected) => Assert.Equal(expected, MemoText.Verdict(memo));

    [Fact]
    public void Extracts_section_until_next_heading_of_same_level()
    {
        Assert.Equal("- [ ] Agregar test de timeout\n- [ ] Validar idempotencia", MemoText.Section(Summary, "Pendientes"));
        Assert.Equal("texto", MemoText.Section(Summary, "otro"));
        Assert.Null(MemoText.Section(Summary, "Riesgos"));
    }

    [Fact]
    public void Nests_headings_but_not_inside_code_fences()
    {
        var input = "## A\n### B\n```\n# comentario\n```\n#tag";
        Assert.Equal("#### A\n##### B\n```\n# comentario\n```\n#tag", MemoText.NestHeadings(input, 4));
        Assert.Equal("##### A", MemoText.NestHeadings("##### A", 4));
    }
}
