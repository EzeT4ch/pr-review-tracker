using Prt.Core.Panel;

namespace Prt.Core.Tests;

public class ReportRendererTests
{
    private static Finding Finding(string title, string recommendation, string? file = "src/Module.Ims/UseCases/MergeCodes.cs", int? line = 773) => new()
    {
        Id = "PERF-1", Severity = Severity.Minor, Title = title, File = file, Line = line, Recommendation = recommendation,
    };

    [Fact]
    public void Findings_table_escapes_cells_shortens_location_and_links_ids()
    {
        var table = ReportRenderer.FindingsTable(
            [Finding("N+1 en `MergeCodes` con IRepository<ProductCode> | a == b", "Una sola query por lote. Después, cachear.")],
            "PR Reviews/acme/api/PR-42/Análisis (run 1)", withStatus: false);

        Assert.Equal(
            "| ID | Sev. | Hallazgo | Ubicación | Acción |\n|---|---|---|---|---|\n" +
            "| [[PR Reviews/acme/api/PR-42/Análisis (run 1)#^perf-1\\|PERF-1]] | 🟡 | N+1 en `MergeCodes` con IRepository\\<ProductCode> \\| a \\=\\= b | `MergeCodes.cs:773` | Una sola query por lote |\n",
            table);
    }

    [Fact]
    public void Findings_table_without_links_or_location_keeps_plain_ids()
    {
        var table = ReportRenderer.FindingsTable([Finding("t", "r", file: null, line: null)], linkTarget: null, withStatus: true);

        Assert.EndsWith("| PERF-1 | 🟡 | t |  | r | nuevo |\n", table);
    }

    [Fact]
    public void Action_is_the_first_sentence_cut_to_fit_a_cell()
    {
        var table = ReportRenderer.FindingsTable(
            [Finding("t", "Extraer un servicio de dominio en Module.Ims.Core que sincronice los códigos de todos los canales; después borrar el resto.")],
            null, withStatus: false);

        Assert.Contains("| Extraer un servicio de dominio en Module.Ims.Core que sincr… |", table);
    }
}
