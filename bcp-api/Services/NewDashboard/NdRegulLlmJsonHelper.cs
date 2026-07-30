using System.Text.Json;
using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.NewDashboard;

public static class NdRegulLlmJsonHelper
{
    private static readonly Regex JsonFenceRegex = new(
        @"```(?:json)?\s*([\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static T ParseJsonObject<T>(string raw, JsonSerializerOptions? options = null)
    {
        var text = ExtractJsonPayload(raw);
        options ??= new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<T>(text, options)
            ?? throw new InvalidOperationException("LLM returned empty JSON object.");
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
