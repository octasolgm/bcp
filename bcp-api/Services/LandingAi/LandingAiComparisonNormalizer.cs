using System.Text.Json;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>Normalizes Landing AI extract JSON into compliance comparison fields.</summary>
public static class LandingAiComparisonNormalizer
{
    public static ComplianceComparisonResult Normalize(JsonElement extraction, string? requirementText = null)
    {
        var raw = extraction;
        if (raw.ValueKind == JsonValueKind.Array && raw.GetArrayLength() > 0)
            raw = raw[0];

        if (raw.ValueKind == JsonValueKind.Object)
        {
            if (raw.TryGetProperty("comparison", out var nested) && nested.ValueKind == JsonValueKind.Object)
                raw = nested;
            else if (raw.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                raw = result;
        }

        if (raw.ValueKind != JsonValueKind.Object)
            return Empty();

        var status = GetString(raw, "comply_status", "status") ?? "Non-Compliant";
        var confidence = ParseConfidence(raw);
        var output = GetString(raw, "uae_response_compliance_level", "output_response") ?? "";

        return Reconcile(new ComplianceComparisonResult
        {
            OutputResponse = output,
            Status = status,
            Confidence = confidence,
            FulfilledClauses = GetString(raw, "fulfilled_clauses"),
            CorrectiveAction = GetString(raw, "corrective_action_plan", "corrective_action"),
            Responsibility = GetString(raw, "suggested_responsibility", "responsibility"),
            ReferencePdf = GetString(raw, "reference_pdf"),
        }, requirementText);
    }

    public static ComplianceComparisonResult Reapply(ComplianceComparisonResult comparison, string? requirementText = null) =>
        Reconcile(comparison, requirementText);

    private static ComplianceComparisonResult Reconcile(ComplianceComparisonResult input, string? requirementText)
    {
        const string noEvidence = "No corresponding procedure found.";
        var output = input.OutputResponse.Trim();
        var status = NormalizeStatus(input.Status);
        var confidence = input.Confidence;

        var lacksEvidence = string.IsNullOrWhiteSpace(output)
            || string.Equals(output.TrimEnd('.'), noEvidence.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

        if (lacksEvidence)
        {
            output = noEvidence;
            if (status is "Compliant" or "Partial Compliant")
                status = "Non-Compliant";
            confidence = Math.Min(confidence, 30);
        }
        else if (confidence == 0)
        {
            confidence = status switch
            {
                "Compliant" => 85,
                "Partial Compliant" => 50,
                _ => 0,
            };
        }

        if (status == "Partial Compliant" && confidence > 85) confidence = 85;
        if (status == "Non-Compliant" && confidence > 30) confidence = 30;

        var corrective = input.CorrectiveAction?.Trim();
        var responsibility = input.Responsibility?.Trim();
        if (status == "Compliant")
        {
            corrective = "";
            responsibility = "";
        }
        else
        {
            corrective ??= "Re-run comparison or verify internal document.";
            responsibility ??= "Compliance Team";
        }

        return new ComplianceComparisonResult
        {
            OutputResponse = output,
            Status = status,
            Confidence = Math.Clamp(confidence, 0, 100),
            FulfilledClauses = string.IsNullOrWhiteSpace(input.FulfilledClauses) ? "None" : input.FulfilledClauses.Trim(),
            CorrectiveAction = corrective,
            Responsibility = responsibility,
            ReferencePdf = input.ReferencePdf,
        };
    }

    private static ComplianceComparisonResult Empty() => new()
    {
        OutputResponse = "No corresponding procedure found.",
        Status = "Non-Compliant",
        Confidence = 0,
        FulfilledClauses = "None",
        CorrectiveAction = "Re-run comparison or verify internal document.",
        Responsibility = "Compliance Team",
    };

    private static string NormalizeStatus(string status) =>
        status.Trim() switch
        {
            "Compliant" => "Compliant",
            "Partial Compliant" => "Partial Compliant",
            "Partially Compliant" => "Partial Compliant",
            _ => "Non-Compliant",
        };

    private static int ParseConfidence(JsonElement raw)
    {
        if (raw.TryGetProperty("compliance_confidence_percentage", out var v) || raw.TryGetProperty("confidence", out v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (int.TryParse(v.GetString(), out var parsed)) return parsed;
        }
        return 0;
    }

    private static string? GetString(JsonElement raw, params string[] names)
    {
        foreach (var name in names)
        {
            if (raw.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }
}
