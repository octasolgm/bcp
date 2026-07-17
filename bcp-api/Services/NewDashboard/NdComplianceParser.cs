using System.Text.RegularExpressions;
using Reguliq.Api.Models;

namespace Reguliq.Api.Services.NewDashboard;

public static class NdComplianceParser
{
    private static readonly Regex ConfidenceRegex = new(@"(\d+)", RegexOptions.Compiled);

    public static string NormalizeStatus(string? status)
    {
        var s = (status ?? "").Trim();
        if (Regex.IsMatch(s, @"^compliant$", RegexOptions.IgnoreCase) && !Regex.IsMatch(s, @"partial", RegexOptions.IgnoreCase))
            return "compliant";
        if (Regex.IsMatch(s, @"partial", RegexOptions.IgnoreCase)) return "partial_compliant";
        if (Regex.IsMatch(s, @"non[- ]?compliant|^no$", RegexOptions.IgnoreCase)) return "non_compliant";
        return "non_compliant";
    }

    public static string ExtractStatusFromMessage(string message)
    {
        var status = ExtractField(message, "Comply Yes/No (Status)", "Status") ?? "Unknown";
        return NormalizeStatus(status);
    }

    public static string? ExtractActionPlan(string message)
    {
        var cap = ExtractField(message, "Corrective Action Plan (CAP)", "CAP", "Action Plan");
        return string.IsNullOrWhiteSpace(cap) || cap is "N/A" or "—" ? null : cap.Trim();
    }

    public static DualVerifyAgreementDto ComparePasses(string landingMessage, string llmMessage) =>
        DualVerifyAgreementService.Compare(landingMessage, llmMessage);

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
}
