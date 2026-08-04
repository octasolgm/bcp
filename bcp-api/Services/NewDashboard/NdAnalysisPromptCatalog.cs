using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.NewDashboard;

public static class NdAnalysisPromptCatalog
{
    public record PromptDefinition(string Key, string Label, string Workflow, string Description, string Text);

    public static IReadOnlyList<PromptDefinition> AllV3Prompts =>
    [
        new(
            "regul_judgment_system",
            "Forward judgment — system prompt",
            "Regul workflow (Analysis V3)",
            "Sent as the system message for every forward-judgment LLM call (one per regulatory clause).",
            NdRegulPromptDefaults.JudgmentSystemPrompt),
        new(
            "regul_judgment_user_context",
            "Forward judgment — user block 1 (policy context)",
            "Regul workflow (Analysis V3)",
            "Sent with each clause. Wraps retrieved internal policy excerpts ({policy_context}). Cacheable when the full manual fits in context.",
            NdRegulPromptDefaults.JudgmentUserContextTemplate),
        new(
            "regul_judgment_user_query",
            "Forward judgment — user block 2 (clause query)",
            "Regul workflow (Analysis V3)",
            "Sent with each clause after block 1. Contains the regulatory clause ({clause_no}, {clause_text}) to judge against the excerpts.",
            NdRegulPromptDefaults.JudgmentUserQueryTemplate),
        new(
            "dual_verify_pass1_v3",
            "Pass 1 — Landing AI compare (V3)",
            "Dual verify V3 (Analysis V2)",
            "Landing AI prompt used on analyse-v9 when comparePromptVersion is v3.",
            LandingAiComparePromptBuilder.GetPromptTemplateV3()),
        new(
            "dual_verify_pass2_v3",
            "Pass 2 — Independent verifier rules (V3)",
            "Dual verify V3 (Analysis V2)",
            "LLM Pass 2 rules appended when re-verifying Landing AI results with V3 judgment standards.",
            DualVerifyPromptBuilder.GetPass2RulesV3Text()),
    ];

    public static PromptDefinition? Find(string key) =>
        AllV3Prompts.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));
}
