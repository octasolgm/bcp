namespace Reguliq.Api.Services.LandingAi;

/// <summary>Backfill policy clauses Landing extract missed by scanning parse markdown headings.</summary>
public static class PolicyClauseMarkdownRecovery
{
    public static List<PolicyClause> MergeMissing(IReadOnlyList<PolicyClause> extracted, string? markdown)
    {
        if (extracted.Count == 0 && string.IsNullOrWhiteSpace(markdown))
            return [];

        var result = extracted.ToList();
        var existing = new HashSet<string>(
            result.Select(c => MarkdownSectionScanner.NormalizeRef(c.ClauseNo)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var scanned in MarkdownSectionScanner.Scan(markdown))
        {
            var key = MarkdownSectionScanner.NormalizeRef(scanned.SectionRef);
            if (existing.Contains(key)) continue;

            result.Add(new PolicyClause(
                scanned.SectionRef,
                PolicyExtractTextSanitizer.Clean(scanned.SectionText),
                0));
            existing.Add(key);
        }

        return PolicyClauseExtractNormalizer.DedupeClauseNumbers(result);
    }
}
