using System.Text.Json;
using System.Text.RegularExpressions;
using Reguliq.Api.Models;

namespace Reguliq.Api.Services;

public static class DualVerifyAgreementService
{
    private static readonly Regex ConfidenceRegex = new(@"(\d+)", RegexOptions.Compiled);

    public static DualVerifyAgreementDto Compare(string landingMessage, string llmMessage)
    {
        var landing = ParseBlock(landingMessage);
        var llm = ParseBlock(llmMessage);

        var landingStatus = NormalizeStatus(landing.Status);
        var llmStatus = NormalizeStatus(llm.Status);
        var landingConf = ParseConfidence(landing.Confidence);
        var llmConf = ParseConfidence(llm.Confidence);
        int? delta = landingConf != null && llmConf != null ? Math.Abs(landingConf.Value - llmConf.Value) : null;

        if (landingStatus == llmStatus)
        {
            if (delta is > 15)
            {
                return new DualVerifyAgreementDto(
                    "confidence_gap", "Status match · confidence differs",
                    landingStatus, llmStatus, landingConf, llmConf, delta,
                    $"Both report {landingStatus}, but confidence differs by {delta} points (Landing {landingConf}% vs LLM {llmConf}%).");
            }
            return new DualVerifyAgreementDto(
                "aligned", "Aligned", landingStatus, llmStatus, landingConf, llmConf, delta,
                $"Both passes agree: {landingStatus}{(landingConf != null ? $" ({landingConf}%)" : "")}.");
        }

        if (landingStatus != "Compliant" && llmStatus != "Compliant")
        {
            return new DualVerifyAgreementDto(
                "both_non_compliant", "Both flag gaps (different severity)",
                landingStatus, llmStatus, landingConf, llmConf, delta,
                $"Landing: {landingStatus}; LLM: {llmStatus}. Review both CAP notes.");
        }

        return new DualVerifyAgreementDto(
            "status_mismatch", "Status mismatch",
            landingStatus, llmStatus, landingConf, llmConf, delta,
            $"Landing AI: {landingStatus}; Second pass: {llmStatus}. Manual review required.");
    }

    private static (string Status, string Confidence) ParseBlock(string message)
    {
        var status = ExtractField(message, "Comply Yes/No (Status)", "Status") ?? "Unknown";
        var confidence = ExtractField(message, "Compliance Confidence %", "Confidence") ?? "";
        return (status, confidence);
    }

    private static string? ExtractField(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var pattern = $@"{Regex.Escape(label)}\s*:\s*(.+?)(?:\r?\n|$)";
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    private static string NormalizeStatus(string status)
    {
        var s = status.Trim();
        if (Regex.IsMatch(s, @"^compliant$", RegexOptions.IgnoreCase) && !Regex.IsMatch(s, @"partial", RegexOptions.IgnoreCase))
            return "Compliant";
        if (Regex.IsMatch(s, @"partial", RegexOptions.IgnoreCase)) return "Partial Compliant";
        if (Regex.IsMatch(s, @"non[- ]?compliant|^no$", RegexOptions.IgnoreCase)) return "Non-Compliant";
        return string.IsNullOrWhiteSpace(s) ? "Unknown" : s;
    }

    private static int? ParseConfidence(string confidence)
    {
        var m = ConfidenceRegex.Match(confidence);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    public static string ToJson(DualVerifyAgreementDto dto) =>
        JsonSerializer.Serialize(dto, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    public static DualVerifyAgreementDto? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<DualVerifyAgreementDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
