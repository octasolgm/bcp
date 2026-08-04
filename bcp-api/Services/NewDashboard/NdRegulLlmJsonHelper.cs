using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.NewDashboard;

public static class NdRegulLlmJsonHelper
{
    private static readonly Regex JsonFenceRegex = new(
        @"```(?:json)?\s*([\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Anthropic tool / Regul prompts use snake_case keys.</summary>
    public static readonly JsonSerializerOptions RegulLlmJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static T ParseJsonObject<T>(string raw, JsonSerializerOptions? options = null)
    {
        var text = ExtractJsonPayload(raw);
        options ??= RegulLlmJsonOptions;
        return JsonSerializer.Deserialize<T>(text, options)
            ?? throw new InvalidOperationException("LLM returned empty JSON object.");
    }

    public static RegulJudgmentResult ParseJudgmentResult(string raw)
    {
        var judgment = ParseJsonObject<RegulJudgmentResult>(raw);
        NormalizeJudgmentResult(judgment);
        return judgment;
    }

    public static void NormalizeJudgmentResult(RegulJudgmentResult judgment)
    {
        if (string.IsNullOrWhiteSpace(judgment.OverallStatus))
            judgment.OverallStatus = !string.IsNullOrWhiteSpace(judgment.DesignStatus)
                ? judgment.DesignStatus.Trim()
                : judgment.OperatingStatus?.Trim() ?? "";

        judgment.PolicyExtract = judgment.PolicyExtract
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();
    }

    public static string ExtractJsonPayload(string raw)
    {
        var trimmed = raw.Trim();
        var fence = JsonFenceRegex.Match(trimmed);
        if (fence.Success)
            trimmed = fence.Groups[1].Value.Trim();

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed.Substring(start, end - start + 1);

        return trimmed;
    }
}
