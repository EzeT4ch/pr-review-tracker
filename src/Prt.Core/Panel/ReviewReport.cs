using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prt.Core.Panel;

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    [JsonStringEnumMemberName("info")] Info = 0,
    [JsonStringEnumMemberName("minor")] Minor = 1,
    [JsonStringEnumMemberName("major")] Major = 2,
    [JsonStringEnumMemberName("blocker")] Blocker = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<Verdict>))]
public enum Verdict
{
    [JsonStringEnumMemberName("approve")] Approve = 0,
    [JsonStringEnumMemberName("approve_with_comments")] ApproveWithComments = 1,
    [JsonStringEnumMemberName("request_changes")] RequestChanges = 2,
    [JsonStringEnumMemberName("block")] Block = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<Confidence>))]
public enum Confidence
{
    [JsonStringEnumMemberName("low")] Low = 0,
    [JsonStringEnumMemberName("medium")] Medium = 1,
    [JsonStringEnumMemberName("high")] High = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingStatus>))]
public enum FindingStatus
{
    [JsonStringEnumMemberName("new")] New = 0,
    [JsonStringEnumMemberName("still_open")] StillOpen = 1,
    [JsonStringEnumMemberName("resolved")] Resolved = 2,
}

public sealed record Finding
{
    public required string Id { get; init; }
    public required Severity Severity { get; init; }
    public required string Title { get; init; }
    public string Category { get; init; } = "";
    public string? File { get; init; }
    public int? Line { get; init; }
    public string Description { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public Confidence Confidence { get; init; } = Confidence.Medium;
    public FindingStatus Status { get; init; } = FindingStatus.New;

    [JsonIgnore]
    public bool IsOpen => Status != FindingStatus.Resolved;

    [JsonIgnore]
    public string Location => File is null ? "" : Line is { } line ? $"{File}:{line}" : File;
}

/// <summary>The normalized, provider-independent output of one reviewer.</summary>
public sealed record ReviewerReport
{
    public required string Reviewer { get; init; }
    public required Verdict Verdict { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    public IReadOnlyList<string> Questions { get; init; } = [];

    [JsonIgnore]
    public IEnumerable<Finding> OpenFindings => Findings.Where(f => f.IsOpen);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ReviewerReport FromJson(string json) =>
        JsonSerializer.Deserialize<ReviewerReport>(json, JsonOptions)
        ?? throw new JsonException("Reporte vacío.");
}

/// <summary>Labels and rules shared by every report, whatever agent produced it.</summary>
public static class ReviewStandard
{
    public static string Label(Severity severity) => severity switch
    {
        Severity.Blocker => "Bloqueante",
        Severity.Major => "Importante",
        Severity.Minor => "Menor",
        _ => "Info",
    };

    public static string Icon(Severity severity) => severity switch
    {
        Severity.Blocker => "🔴",
        Severity.Major => "🟠",
        Severity.Minor => "🟡",
        _ => "🔵",
    };

    /// <summary>Same wording as the session memo verdicts, so PR verdicts stay comparable.</summary>
    public static string Label(Verdict verdict) => verdict switch
    {
        Verdict.Block => "BLOQUEADO",
        Verdict.RequestChanges => "CAMBIOS REQUERIDOS",
        Verdict.ApproveWithComments => "APROBAR CON CAMBIOS",
        _ => "APROBAR",
    };

    public static string Label(Confidence confidence) => confidence switch
    {
        Confidence.High => "alta",
        Confidence.Low => "baja",
        _ => "media",
    };

    public static string Label(FindingStatus status) => status switch
    {
        FindingStatus.StillOpen => "sigue abierto",
        FindingStatus.Resolved => "resuelto",
        _ => "nuevo",
    };

    /// <summary>The verdict open findings force, regardless of what the agent said.</summary>
    public static Verdict MinimumVerdict(IEnumerable<Finding> findings)
    {
        var worst = findings.Where(f => f.IsOpen).Select(f => (Severity?)f.Severity).Max();
        return worst switch
        {
            Severity.Blocker => Verdict.Block,
            Severity.Major => Verdict.RequestChanges,
            Severity.Minor => Verdict.ApproveWithComments,
            _ => Verdict.Approve,
        };
    }

    public static Verdict Combine(IEnumerable<Verdict> verdicts) =>
        verdicts.DefaultIfEmpty(Verdict.Approve).Max();

    /// <summary>Instructions about the response format. Fixed: the parser depends on it.</summary>
    public static string ResponseFormat(string reviewer, string idPrefix) =>
        $$"""
        Respondé ÚNICAMENTE con un objeto JSON válido, sin texto antes ni después (podés envolverlo en un bloque ```json).
        Esquema:
        {
          "reviewer": "{{reviewer}}",
          "verdict": "approve | approve_with_comments | request_changes | block",
          "summary": "2 a 4 oraciones con la conclusión de tu revisión",
          "findings": [
            {
              "id": "{{idPrefix}}-1",
              "severity": "blocker | major | minor | info",
              "category": "subtema dentro de tu foco",
              "title": "resumen de hasta 80 caracteres",
              "file": "ruta/relativa/al/repo o null",
              "line": 42,
              "description": "qué pasa, en qué escenario y por qué importa, con evidencia del código",
              "recommendation": "qué cambiar concretamente",
              "confidence": "high | medium | low",
              "status": "new | still_open | resolved"
            }
          ],
          "questions": ["decisión que requiere criterio humano, redactada para responderse sin releer el PR"]
        }
        Reglas: ids con el prefijo {{idPrefix}} y numeración correlativa; "line" es un número o null; si no hay hallazgos,
        "findings" es [] y "verdict" es "approve"; el veredicto debe ser coherente con la severidad más alta abierta.
        """;
}
