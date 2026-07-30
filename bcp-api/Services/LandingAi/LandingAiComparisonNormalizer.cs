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
            if (IsWeakCorrectivePlan(corrective))
                corrective = BuildFallbackCorrectivePlan(requirementText, status);
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

    /// <summary>
    /// Empty / placeholder CAPs (e.g. "Re-run comparison…") leave the UI with no real gap or action.
    /// Build a structured Gap(s) plan from the regulatory requirement so makers see what is missing.
    /// </summary>
    internal static bool IsWeakCorrectivePlan(string? corrective)
    {
        if (string.IsNullOrWhiteSpace(corrective)) return true;
        var t = corrective.Trim();
        if (t is "—" or "-" or "N/A" or "n/a" or "None" or "none") return true;
        if (t.Contains("Re-run comparison", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("verify internal document", StringComparison.OrdinalIgnoreCase)
            && !t.Contains("Missing:", StringComparison.OrdinalIgnoreCase))
            return true;
        // Literal "Missing: MISSING" with no real intent
        if (System.Text.RegularExpressions.Regex.IsMatch(
                t, @"Missing:\s*MISSING\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    internal static string BuildFallbackCorrectivePlan(string? requirementText, string status)
    {
        var intent = SummarizeRequirementIntent(requirementText);
        var priority = status.Contains("Partial", StringComparison.OrdinalIgnoreCase) ? "Medium" : "Higher";
        return
            $"Gap(s):\n(1) Missing: No equivalent internal procedure covers — {intent}. " +
            $"Fix: Draft, approve, and implement an internal control/procedure that addresses this requirement; " +
            $"assign an owner and retain evidence of implementation. Priority: {priority}.";
    }

    private static string SummarizeRequirementIntent(string? requirementText)
    {
        var text = (requirementText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "the stated regulatory obligation";

        // Prefer first sentence / clause; keep short for the Missing field.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        var cut = text.IndexOfAny(['.', ';', '\n']);
        if (cut > 40 && cut < 220) text = text[..cut].Trim();
        if (text.Length > 220) text = text[..217].TrimEnd() + "…";
        return text;
    }

    private static ComplianceComparisonResult Empty() => new()
    {
        OutputResponse = "No corresponding procedure found.",
        Status = "Non-Compliant",
        Confidence = 0,
        FulfilledClauses = "None",
        CorrectiveAction = BuildFallbackCorrectivePlan(null, "Non-Compliant"),
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
