using System.Text.Json;

namespace Reguliq.Api.Services.LandingAi;

public sealed record PolicyClause(string ClauseNo, string ClauseText, int SourcePage);

public static class PolicyClauseParser
{
    public static List<PolicyClause> ParseFromExtractJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseFromExtraction(doc.RootElement);
    }

    public static List<PolicyClause> ParseFromExtraction(JsonElement extraction)
    {
        var list = new List<PolicyClause>();
        if (!extraction.TryGetProperty("clauses", out var clauses) || clauses.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var c in clauses.EnumerateArray())
        {
            var no = c.TryGetProperty("clause_no", out var cn) ? cn.GetString() : null;
            var text = c.TryGetProperty("clause_text", out var ct) ? ct.GetString() : null;
            if (string.IsNullOrWhiteSpace(no) || string.IsNullOrWhiteSpace(text)) continue;

            var page = 0;
            if (c.TryGetProperty("source_page", out var sp) && sp.ValueKind == JsonValueKind.Number && sp.TryGetInt32(out var p))
                page = p;

            list.Add(new PolicyClause(no.Trim(), text.Trim(), page));
        }

        return list;
    }
}
