using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Classifies extracted regulation points — keep in sync with bcp-web/src/lib/gov-point-filter.ts
/// </summary>
public static class GovPointClassifier
{
    private static readonly Regex ObligationPattern = new(
        @"\b(must|shall|should|required to|obliged to|ensure that|are required|need to|have to|lfis should|lfi should)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] InfoTitlePatterns =
    [
        @"^introduction$", @"^introductory\b", @"^foreword$", @"^preface$",
        @"^table of contents$", @"^contents$", @"^document history$",
        @"^version history$", @"^revision history$", @"^acknowledg", @"^disclaimer$",
        @"^about this (document|guidance)$", @"^overview$", @"^background$",
        @"^applicability$", @"^scope$",
    ];

    public static bool IsAnnexPoint(string pointId, string? title, string? section)
    {
        var id = pointId.Trim();
        var sec = (section ?? "").Trim();
        var t = (title ?? "").Trim();

        if (Regex.IsMatch(sec, @"^annexes?\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(sec, @"^annex\s+\d+", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(sec, @"\bannex\s+\d+\s*·", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(id, @"^annexes?\s*-", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(sec, @"^\d+\.\s+(?:Red Flag Indicators|Lessons learned)", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(sec, @"red flag indicators for (tf|pf)\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(t, @"^red flag indicators for (tf|pf)\b", RegexOptions.IgnoreCase)) return true;
        if (t.Contains("FATF Typologies Report on Proliferation Financing", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(id, @"^\([ivxlcdm]+\)$", RegexOptions.IgnoreCase)
            && (Regex.IsMatch(sec, @"^\d+\.\s+(?:Red Flag Indicators|Lessons learned)", RegexOptions.IgnoreCase)
                || Regex.IsMatch(sec, @"red flag", RegexOptions.IgnoreCase)))
            return true;
        if (id == "1" && Regex.IsMatch(sec, @"annex\s+1", RegexOptions.IgnoreCase) && Regex.IsMatch(t, @"red flag", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    public static bool IsSectionOnePoint(string pointId, string? section)
    {
        var id = pointId.Trim();
        if (Regex.IsMatch(id, @"^1(\.|$)")) return true;
        if (Regex.IsMatch(id, @"^1\.\d")) return true;
        var top = Regex.Match(id, @"^(\d+)");
        if (top.Success && top.Groups[1].Value == "1") return true;
        if (Regex.IsMatch((section ?? "").Trim(), @"^1(\.|\s)")) return true;
        return false;
    }

    public static bool IsIntroductionPoint(
        string pointId,
        string? title,
        string text,
        string? section,
        string? pointType)
    {
        if (IsAnnexPoint(pointId, title, section)) return false;

        var id = pointId.Trim();
        var sec = (section ?? "").Trim();
        var t = (title ?? "").Trim();
        var body = text.Trim();

        if (IsSectionOnePoint(id, section))
            return true;

        if (string.Equals(pointType, "informational", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Regex.IsMatch(t, @"^purpose$", RegexOptions.IgnoreCase)
            && Regex.IsMatch(body, @"^the purpose of this", RegexOptions.IgnoreCase)
            && !ObligationPattern.IsMatch(body))
            return true;

        if (id.Contains("purpose of this guidance - purpose", StringComparison.OrdinalIgnoreCase))
            return true;
        if (id.Contains("purpose of this guidance - applicability", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var pattern in InfoTitlePatterns)
        {
            if (Regex.IsMatch(t, pattern, RegexOptions.IgnoreCase))
                return true;
        }

        if (Regex.IsMatch(sec, @"^introduction\b", RegexOptions.IgnoreCase) && !ObligationPattern.IsMatch(body))
            return true;

        if (!ObligationPattern.IsMatch(body)
            && (Regex.IsMatch(body, @"^(unless otherwise noted,\s*)?this guidance applies to\b", RegexOptions.IgnoreCase)
                || (Regex.IsMatch(t, @"^applicability$", RegexOptions.IgnoreCase) && !ObligationPattern.IsMatch(body))))
            return true;

        if (body.Length < 400
            && Regex.IsMatch(body, @"\b(means|refers to|is defined as|is a technique|is an algorithm)\b", RegexOptions.IgnoreCase)
            && !ObligationPattern.IsMatch(body))
            return true;

        if (body.Length > 80 && !ObligationPattern.IsMatch(body))
        {
            if (Regex.IsMatch(body, @"^the purpose of", RegexOptions.IgnoreCase)
                || Regex.IsMatch(body, @"^this document (describes|provides|sets out)", RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a gov point should be included in gap / Arena analysis exports.
    /// Keep in sync with bcp-web/src/lib/gov-point-filter.ts classifyGovPoint.
    /// </summary>
    public static bool IsComparableForAnalysis(
        string pointId,
        string? title,
        string text,
        string? section,
        string? pointType)
    {
        if (string.Equals(pointType, "mandatory", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IsAnnexPoint(pointId, title, section))
            return false;
        if (IsSectionOnePoint(pointId, section))
            return false;
        if (string.Equals(pointType, "informational", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pointType, "definition", StringComparison.OrdinalIgnoreCase))
            return false;
        return !IsIntroductionPoint(pointId, title, text, section, pointType);
    }
}
