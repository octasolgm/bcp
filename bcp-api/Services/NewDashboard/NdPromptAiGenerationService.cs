using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.Llm;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Uses an admin-selected LLM to merge reviewer suggestions into a prompt template,
/// producing a candidate new version plus a self-reported coverage check per suggestion.
/// </summary>
public class NdPromptAiGenerationService(
    Reguliq.Api.Services.GeminiService gemini,
    OpenAiCompatibleLlmClient openAi,
    AnthropicLlmClient anthropic,
    XAiLlmClient xAi,
    ILogger<NdPromptAiGenerationService> logger)
{
    public record CoverageResult(Guid SuggestionId, bool Covered);
    public record GenerationResult(string PromptText, IReadOnlyList<CoverageResult> Coverage);

    public async Task<GenerationResult> GenerateAsync(
        string basePromptText,
        IReadOnlyList<NdAnalysisPromptSuggestion> suggestions,
        string provider,
        string? model,
        string? instruction,
        CancellationToken ct = default)
    {
        if (suggestions.Count == 0)
            throw new InvalidOperationException("Select at least one suggestion to apply.");

        if (!LlmProviderCatalog.TryGet(provider, out var def))
            throw new InvalidOperationException($"Unknown LLM provider '{provider}'.");

        var resolvedModel = string.IsNullOrWhiteSpace(model) ? def.DefaultModel : model.Trim();

        var metaPrompt = BuildMetaPrompt(basePromptText, suggestions, instruction);

        logger.LogInformation(
            "Generating prompt version via {Provider}/{Model} using {Count} suggestion(s)",
            def.Id, resolvedModel, suggestions.Count);

        var raw = def.Id.ToLowerInvariant() switch
        {
            "google" => await gemini.AnalyzeTextAsync(metaPrompt, resolvedModel, ct),
            "openai" => await openAi.AnalyzeTextAsync(metaPrompt, resolvedModel, ct),
            "anthropic" => await anthropic.AnalyzeTextAsync(metaPrompt, resolvedModel, ct),
            "xai" => await xAi.AnalyzeTextAsync(metaPrompt, resolvedModel, ct),
            _ => throw new InvalidOperationException($"Unsupported LLM provider '{def.Id}'."),
        };

        return ParseResponse(raw, suggestions);
    }

    private static string BuildMetaPrompt(
        string basePromptText,
        IReadOnlyList<NdAnalysisPromptSuggestion> suggestions,
        string? instruction)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert prompt engineer improving an AI prompt template used in a regulatory compliance analysis pipeline.");
        sb.AppendLine();
        sb.AppendLine("BASE PROMPT (current version):");
        sb.AppendLine("\"\"\"");
        sb.AppendLine(basePromptText);
        sb.AppendLine("\"\"\"");
        sb.AppendLine();
        sb.AppendLine("Reviewers submitted the following improvement suggestions. Rewrite the base prompt so the INTENT of EVERY suggestion below is clearly reflected — integrate them naturally into the wording and structure rather than just appending a list.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL RULES:");
        sb.AppendLine("- Preserve every literal placeholder tag exactly as written (e.g. {policy_context}, {clause_no}, {clause_text}). Never remove, rename, translate, or reformat these tags.");
        sb.AppendLine("- Keep the overall purpose and tone of the base prompt intact.");
        sb.AppendLine("- Do not invent new placeholder tags.");
        sb.AppendLine();
        sb.AppendLine("Suggestions:");
        var i = 1;
        foreach (var s in suggestions)
        {
            sb.AppendLine($"{i}. [id={s.Id}] {s.Comment}");
            i++;
        }

        if (!string.IsNullOrWhiteSpace(instruction))
        {
            sb.AppendLine();
            sb.AppendLine($"Additional instruction from the admin (optimize/modify request): {instruction.Trim()}");
        }

        sb.AppendLine();
        sb.AppendLine("Respond with ONLY strict JSON (no markdown code fences, no commentary), in exactly this shape:");
        sb.AppendLine("{\"promptText\": \"<full revised prompt text>\", \"coverage\": [{\"suggestionId\": \"<guid>\", \"covered\": true}]}");
        sb.AppendLine("Include one coverage entry per suggestion id listed above. Mark \"covered\": true only if that suggestion's idea is clearly reflected in promptText.");
        return sb.ToString();
    }

    private static GenerationResult ParseResponse(
        string raw,
        IReadOnlyList<NdAnalysisPromptSuggestion> suggestions)
    {
        var json = ExtractJson(raw);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The AI did not return valid JSON. Try again or a different model. ({ex.Message})");
        }

        var promptText = node?["promptText"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(promptText))
            throw new InvalidOperationException("The AI response did not include promptText.");

        var coverageBySuggestion = new Dictionary<Guid, bool>();
        if (node?["coverage"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var idStr = item?["suggestionId"]?.GetValue<string>();
                var covered = item?["covered"]?.GetValue<bool>() ?? false;
                if (Guid.TryParse(idStr, out var id))
                    coverageBySuggestion[id] = covered;
            }
        }

        var coverage = suggestions
            .Select(s => new CoverageResult(
                s.Id,
                coverageBySuggestion.TryGetValue(s.Id, out var covered)
                    ? covered
                    : promptText.Contains(FirstFewWords(s.Comment), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new GenerationResult(promptText, coverage);
    }

    private static string FirstFewWords(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(Math.Min(4, words.Length)));
    }

    private static string ExtractJson(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0) trimmed = trimmed[(firstNewline + 1)..];
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) trimmed = trimmed[..lastFence];
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed[start..(end + 1)];

        return trimmed;
    }
}
