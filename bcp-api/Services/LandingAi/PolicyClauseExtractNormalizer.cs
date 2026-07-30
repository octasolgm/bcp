namespace Reguliq.Api.Services.LandingAi;

/// <summary>Matches Regul.ai dedupe_clause_numbers() — unique clause_no per section.</summary>
public static class PolicyClauseExtractNormalizer
{
    public static List<PolicyClause> DedupeClauseNumbers(IReadOnlyList<PolicyClause> clauses)
    {
        if (clauses.Count == 0) return [];

        var counts = clauses.GroupBy(c => c.ClauseNo).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var deduped = new List<PolicyClause>();

        foreach (var c in clauses)
        {
            if (counts[c.ClauseNo] > 1)
            {
                seen[c.ClauseNo] = seen.GetValueOrDefault(c.ClauseNo) + 1;
                deduped.Add(c with { ClauseNo = $"{c.ClauseNo}-{seen[c.ClauseNo]}" });
            }
            else
            {
                deduped.Add(c);
            }
        }

        return deduped;
    }
}
