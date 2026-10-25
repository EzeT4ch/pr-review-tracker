using Prt.Core.Panel;

namespace Prt.Core.Tests;

public class ReportParserTests
{
    private const string Valid =
        """
        {
          "reviewer": "security",
          "verdict": "request_changes",
          "summary": "Falta autorización.",
          "findings": [
            { "id": "SEC-1", "severity": "minor", "title": "Log con email", "file": "src/Log.cs", "line": 10,
              "description": "d", "recommendation": "r", "confidence": "high", "status": "new" },
            { "id": "SEC-2", "severity": "major", "title": "Endpoint sin auth", "file": "src\\Api.cs", "line": "42-50",
              "description": "d", "recommendation": "r", "confidence": "medium", "status": "still_open" }
          ],
          "questions": ["¿El endpoint es público por diseño?"]
        }
        """;

    [Fact]
    public void Parses_and_normalizes_a_valid_report()
    {
        var result = ReportParser.Parse(Valid, "security", "SEC");

        Assert.True(result.Success);
        var report = result.Report!;
        Assert.Equal(Verdict.RequestChanges, report.Verdict);
        Assert.Equal(["SEC-2", "SEC-1"], report.Findings.Select(f => f.Id));
        Assert.Equal("src/Api.cs", report.Findings[0].File);
        Assert.Equal(42, report.Findings[0].Line);
        Assert.Equal(FindingStatus.StillOpen, report.Findings[0].Status);
        Assert.Equal(Confidence.High, report.Findings[1].Confidence);
        Assert.Single(report.Questions);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Extracts_json_from_prose_and_fenced_blocks()
    {
        var answer = "Listo, acá va el reporte:\n\n```json\n" + Valid + "\n```\n\nCualquier cosa avisame.";
        Assert.True(ReportParser.Parse(answer, "security", "SEC").Success);

        var bare = "Reporte: " + Valid.ReplaceLineEndings(" ") + " fin";
        Assert.True(ReportParser.Parse(bare, "security", "SEC").Success);
    }

    [Fact]
    public void Accepts_synonyms_and_fixes_ids()
    {
        var answer =
            """
            {"verdict":"LGTM","summary":"s","findings":[
              {"severity":"critical","title":"a"},
              {"id":"x-9","severity":"HIGH","title":"b","line":null,"file":"null"},
              {"id":"PERF-1","severity":"nit","title":"c"}
            ]}
            """;

        var report = ReportParser.Parse(answer, "performance", "PERF").Report!;

        Assert.Equal([Severity.Blocker, Severity.Major, Severity.Info], report.Findings.Select(f => f.Severity));
        Assert.Equal(["PERF-2", "PERF-3", "PERF-1"], report.Findings.Select(f => f.Id));
        Assert.Null(report.Findings[1].File);
        Assert.Null(report.Findings[1].Line);
        // "LGTM" with a blocker open is escalated to block.
        Assert.Equal(Verdict.Block, report.Verdict);
    }

    [Fact]
    public void Resolved_findings_do_not_raise_the_verdict()
    {
        var answer = """{"verdict":"approve","summary":"s","findings":[{"id":"BUG-1","severity":"blocker","title":"t","status":"resolved"}]}""";

        var result = ReportParser.Parse(answer, "bugs", "BUG");

        Assert.Equal(Verdict.Approve, result.Report!.Verdict);
        Assert.Empty(result.Report.OpenFindings);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Missing_verdict_is_derived_and_bad_items_are_warned()
    {
        var answer = """{"findings":[{"severity":"weird","title":"t"},{"severity":"major"},"texto"]}""";

        var result = ReportParser.Parse(answer, "bugs", "BUG");

        Assert.Equal(Verdict.ApproveWithComments, result.Report!.Verdict);
        Assert.Single(result.Report.Findings);
        Assert.Equal(3, result.Warnings.Count);
    }

    [Theory]
    [InlineData("No encontré problemas.")]
    [InlineData("""{"verdict":"approve","summary":"ok"}""")]
    [InlineData("""{"findings": "ninguno"}""")]
    public void Rejects_answers_without_the_standard_shape(string answer)
    {
        var result = ReportParser.Parse(answer, "bugs", "BUG");
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Normalized_report_round_trips_through_json()
    {
        var report = ReportParser.Parse(Valid, "security", "SEC").Report!;
        var json = report.ToJson();

        Assert.Contains("\"verdict\": \"request_changes\"", json);
        Assert.Contains("\"status\": \"still_open\"", json);
        Assert.DoesNotContain("open_findings", json);
        Assert.Equal(report.Findings, ReviewerReport.FromJson(json).Findings);
    }

    [Fact]
    public void Combines_verdicts_by_worst_case()
    {
        Assert.Equal(Verdict.RequestChanges, ReviewStandard.Combine([Verdict.Approve, Verdict.RequestChanges, Verdict.ApproveWithComments]));
        Assert.Equal(Verdict.Approve, ReviewStandard.Combine([]));
        Assert.Equal("CAMBIOS REQUERIDOS", ReviewStandard.Label(Verdict.RequestChanges));
    }
}
