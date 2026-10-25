using System.Text.Json;
using System.Text.RegularExpressions;

namespace Prt.Core.Panel;

public sealed record ParseResult(ReviewerReport? Report, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool Success => Report is not null;
}

/// <summary>
/// Turns an agent's free-form answer into a <see cref="ReviewerReport"/>. Tolerant with what agents
/// commonly do (fenced blocks, synonyms, numbers as strings) and strict with what the report needs.
/// </summary>
public static partial class ReportParser
{
    [GeneratedRegex(@"```(?:json|JSON)?\s*\n(?<body>[\s\S]*?)\n?```")]
    private static partial Regex FencedBlock();

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstNumber();

    public static ParseResult Parse(string answer, string reviewer, string idPrefix)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (FindJson(answer) is not { } root)
            return new ParseResult(null, ["La respuesta no contiene un objeto JSON válido."], warnings);

        using (root)
        {
            var obj = root.RootElement;
            if (!obj.TryGetProperty("findings", out var findingsElement) || findingsElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add("Falta el arreglo \"findings\".");
                return new ParseResult(null, errors, warnings);
            }

            var findings = new List<Finding>();
            // Ids that already follow the standard are kept (they track the same finding across rounds),
            // so they are reserved before numbering the rest.
            var reserved = findingsElement.EnumerateArray()
                .Where(i => i.ValueKind == JsonValueKind.Object)
                .Select(i => Text(i, "id").ToUpperInvariant())
                .Where(id => IsStandardId(id, idPrefix))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var item in findingsElement.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add($"Hallazgo {index} ignorado: no es un objeto.");
                    continue;
                }

                var title = Text(item, "title");
                var description = Text(item, "description");
                if (title.Length == 0 && description.Length == 0)
                {
                    warnings.Add($"Hallazgo {index} ignorado: sin título ni descripción.");
                    continue;
                }

                var severityText = Text(item, "severity");
                var severity = ParseSeverity(severityText);
                if (severity is null)
                {
                    warnings.Add($"Hallazgo {index}: severidad '{severityText}' desconocida, se usa 'minor'.");
                    severity = Severity.Minor;
                }

                var id = NormalizeId(Text(item, "id"), idPrefix, usedIds, reserved);
                usedIds.Add(id);
                findings.Add(new Finding
                {
                    Id = id,
                    Severity = severity.Value,
                    Title = title.Length > 0 ? Truncate(title, 120) : Truncate(description, 80),
                    Category = Text(item, "category"),
                    File = Text(item, "file") is { Length: > 0 } file && !IsNullWord(file) ? file.Replace('\\', '/') : null,
                    Line = ParseLine(item),
                    Description = description,
                    Recommendation = Text(item, "recommendation"),
                    Confidence = ParseConfidence(Text(item, "confidence")),
                    Status = ParseStatus(Text(item, "status")),
                });
            }

            var minimum = ReviewStandard.MinimumVerdict(findings);
            var verdictText = Text(obj, "verdict");
            var verdict = ParseVerdict(verdictText);
            if (verdict is null)
            {
                if (verdictText.Length > 0)
                    warnings.Add($"Veredicto '{verdictText}' desconocido; se deriva de los hallazgos.");
                verdict = minimum;
            }
            else if (verdict < minimum)
            {
                warnings.Add($"Veredicto '{verdictText}' inconsistente con los hallazgos abiertos; se ajusta.");
                verdict = minimum;
            }

            var questions = obj.TryGetProperty("questions", out var q) && q.ValueKind == JsonValueKind.Array
                ? q.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim() : e.ToString())
                    .Where(s => s.Length > 0).ToList()
                : [];

            var report = new ReviewerReport
            {
                Reviewer = reviewer,
                Verdict = verdict.Value,
                Summary = Text(obj, "summary"),
                Findings = findings
                    .OrderBy(f => f.Status == FindingStatus.Resolved)
                    .ThenByDescending(f => f.Severity)
                    .ToList(),
                Questions = questions,
            };
            return new ParseResult(report, errors, warnings);
        }
    }

    private static JsonDocument? FindJson(string answer)
    {
        var candidates = new List<string> { answer.Trim() };
        candidates.AddRange(FencedBlock().Matches(answer).Select(m => m.Groups["body"].Value).Reverse());
        var first = answer.IndexOf('{');
        var last = answer.LastIndexOf('}');
        if (first >= 0 && last > first)
            candidates.Add(answer[first..(last + 1)]);

        foreach (var candidate in candidates)
        {
            try
            {
                var doc = JsonDocument.Parse(candidate, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    return doc;
                doc.Dispose();
            }
            catch (JsonException)
            {
                // Try the next candidate.
            }
        }
        return null;
    }

    internal static Severity? ParseSeverity(string text) => Normalize(text) switch
    {
        "blocker" or "blocking" or "critical" or "critica" or "bloqueante" => Severity.Blocker,
        "major" or "high" or "alta" or "importante" or "mayor" => Severity.Major,
        "minor" or "medium" or "moderate" or "media" or "menor" or "low" or "baja" => Severity.Minor,
        "info" or "informational" or "nit" or "trivial" or "suggestion" or "note" => Severity.Info,
        _ => null,
    };

    internal static Verdict? ParseVerdict(string text) => Normalize(text) switch
    {
        "approve" or "approved" or "aprobar" or "lgtm" => Verdict.Approve,
        "approve_with_comments" or "comment" or "comments" or "aprobar_con_cambios" or "aprobar_con_comentarios" => Verdict.ApproveWithComments,
        "request_changes" or "changes_requested" or "needs_changes" or "cambios_requeridos" => Verdict.RequestChanges,
        "block" or "blocked" or "reject" or "bloqueado" or "bloquear" => Verdict.Block,
        _ => null,
    };

    private static Confidence ParseConfidence(string text) => Normalize(text) switch
    {
        "high" or "alta" => Confidence.High,
        "low" or "baja" => Confidence.Low,
        _ => Confidence.Medium,
    };

    private static FindingStatus ParseStatus(string text) => Normalize(text) switch
    {
        "still_open" or "open" or "abierto" or "sigue_abierto" or "unresolved" => FindingStatus.StillOpen,
        "resolved" or "fixed" or "resuelto" or "closed" => FindingStatus.Resolved,
        _ => FindingStatus.New,
    };

    private static int? ParseLine(JsonElement item)
    {
        if (!item.TryGetProperty("line", out var line))
            return null;
        if (line.ValueKind == JsonValueKind.Number && line.TryGetInt32(out var n))
            return n > 0 ? n : null;
        if (line.ValueKind == JsonValueKind.String && FirstNumber().Match(line.GetString()!) is { Success: true } m &&
            int.TryParse(m.Value, out var parsed))
            return parsed > 0 ? parsed : null;
        return null;
    }

    private static bool IsStandardId(string id, string prefix) =>
        Regex.IsMatch(id, $"^{Regex.Escape(prefix)}-\\d+$", RegexOptions.IgnoreCase);

    private static string NormalizeId(string id, string prefix, HashSet<string> used, HashSet<string> reserved)
    {
        id = id.Trim().ToUpperInvariant();
        if (IsStandardId(id, prefix) && !used.Contains(id))
            return id;

        var next = 1;
        while (used.Contains($"{prefix}-{next}") || reserved.Contains($"{prefix}-{next}"))
            next++;
        return $"{prefix}-{next}";
    }

    private static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!.Trim(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => "",
        };
    }

    private static string Normalize(string text) =>
        text.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_')
            .Replace('í', 'i').Replace('á', 'a').Replace('é', 'e').Replace('ó', 'o').Replace('ú', 'u');

    private static bool IsNullWord(string text) => text is "null" or "none" or "n/a" or "-";

    private static string Truncate(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }
}
