using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Gap verification (retrieval pipeline v5), the pure part: reads the gap lines of a judgment, builds the short
/// question asked for each gap, reads the answer, and rewrites the judgment when a gap turns out to be covered.
/// The search and the AI call live in <see cref="NdRegulAnalysisProcessor"/>. A gap is only removed when the
/// answer quotes text that is verbatim in the excerpt it names, so an unsupported "covered" never hides a gap.
/// </summary>
public static partial class NdRegulGapVerifier
{
    public sealed record Gap(int Number, string Line, string Requirement, string? ClauseWords);

    public sealed record Excerpt(string Id, string Label, string Text);

    public sealed class Answer
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("evidence")]
        public string Evidence { get; set; } = "";

        [JsonPropertyName("quote")]
        public string Quote { get; set; } = "";

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";
    }

    public sealed record Outcome(Gap Gap, string Status, Excerpt? Evidence, string Quote, string Reason);

    [GeneratedRegex(@"(?:^|\n)\s*\[(\d+)\]\s*")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\(clause:\s*[""“]?(?<words>.+?)[""”]?\)", RegexOptions.IgnoreCase)]
    private static partial Regex ClauseWords();

    [GeneratedRegex(@"\s+[-–—]\s+Missing:.*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MissingTail();

    /// <summary>"[n] ..." lines of a gap_description (or suggested_action); a line may span several text lines.</summary>
    public static IReadOnlyList<(int Number, string Text)> NumberedLines(string text)
    {
        var normalized = (text ?? "").Replace("\r\n", "\n");
        var markers = Marker().Matches(normalized);
        var lines = new List<(int, string)>();
        for (var i = 0; i < markers.Count; i++)
        {
            var start = markers[i].Index + markers[i].Length;
            var end = i + 1 < markers.Count ? markers[i + 1].Index : normalized.Length;
            var body = normalized[start..end].Trim();
            if (body.Length > 0) lines.Add((int.Parse(markers[i].Groups[1].Value), body));
        }

        return lines;
    }

    public static IReadOnlyList<Gap> ParseGaps(string gapDescription)
    {
        var text = (gapDescription ?? "").Trim();
        if (text.Length == 0 || text == "N/A") return [];
        var numbered = NumberedLines(text);
        if (numbered.Count == 0) numbered = [(1, text)];
        return numbered.Select(l =>
        {
            var words = ClauseWords().Match(l.Text);
            var requirement = MissingTail().Replace(l.Text, "");
            requirement = ClauseWords().Replace(requirement, "").Trim();
            return new Gap(l.Number, l.Text, requirement.Length > 0 ? requirement : l.Text, words.Success ? words.Groups["words"].Value.Trim() : null);
        }).ToList();
    }

    [GeneratedRegex(@"\(clause:\s*[""“](?<words>.*?)[""”]\)(?=\s*[-–—]\s*Missing:)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex QuotedClauseWords();

    /// <summary>Clause words quoted in a gap line longer than this are shortened to their first words.</summary>
    public const int MaxClauseWordsInGap = 25;

    /// <summary>
    /// Gap and action lines keyed 1..n in the order the gaps are listed. The AI sometimes keys gaps by requirement
    /// number ([2], [4]) while every screen lists gaps as 1, 2, ...; the actions then attach to the wrong gap.
    /// Actions follow their gap's new number (or, when their keys match no gap but there is one key per gap, the
    /// order of first appearance). A clause quote longer than <see cref="MaxClauseWordsInGap"/> words inside a gap
    /// line is cut to its first words, so the gap reads as a statement and not as a copy of the clause.
    /// </summary>
    public static RegulJudgmentResult NormalizeGapNumbering(RegulJudgmentResult judgment)
    {
        var gaps = NumberedLines(judgment.GapDescription);
        if (gaps.Count == 0) return judgment;

        var newByOld = new Dictionary<int, int>();
        for (var i = 0; i < gaps.Count; i++) newByOld.TryAdd(gaps[i].Number, i + 1);
        judgment.GapDescription = string.Join("\n", gaps.Select((g, i) => $"[{i + 1}] {ShortenClauseWords(g.Text)}"));

        var sequential = gaps.Select((g, i) => g.Number == i + 1).All(x => x);
        var actions = NumberedLines(judgment.SuggestedAction);
        if (sequential || actions.Count == 0) return judgment;

        Dictionary<int, int>? actionMap = null;
        if (actions.All(a => newByOld.ContainsKey(a.Number)))
        {
            actionMap = newByOld;
        }
        else
        {
            var keys = actions.Select(a => a.Number).Distinct().ToList();
            if (keys.Count == gaps.Count)
                actionMap = keys.Select((k, i) => (k, i + 1)).ToDictionary(x => x.k, x => x.Item2);
        }

        if (actionMap != null)
            judgment.SuggestedAction = string.Join("\n", actions.Select(a => $"[{actionMap[a.Number]}] {a.Text}"));
        return judgment;
    }

    private static string ShortenClauseWords(string gapLine) =>
        QuotedClauseWords().Replace(gapLine, m =>
        {
            var words = m.Groups["words"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= MaxClauseWordsInGap) return m.Value;
            return $"(clause: \"{string.Join(' ', words.Take(15)).TrimEnd(',', ';', '.')} ...\")";
        });

    /// <summary>Search queries for one gap: the missing requirement and the clause words it comes from.</summary>
    public static IReadOnlyList<string> QueriesFor(Gap gap) =>
        new[] { gap.Requirement, gap.ClauseWords }.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q!).Distinct().ToList();

    public static string BuildPrompt(string clauseNo, string clauseText, Gap gap, IReadOnlyList<Excerpt> excerpts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a senior regulatory compliance analyst double-checking ONE gap found in an analysis of a regulatory clause against a financial institution's internal documents.");
        sb.AppendLine("Decide whether any excerpt below already covers the missing requirement. Judge by meaning and legal outcome, not by keywords: any wording, section or document counts; equivalent terms count (abbreviations, synonyms, the institution's own name for itself); for the scope or interpretation a clause states, the concept applied in practice (typologies, red flags, risk factors, procedures) counts. Exception: when the gap is that a term's definition is missing, only an excerpt that states that definition (any wording, same meaning) or adopts the law's definition by reference covers it; uses of the term, examples, typologies or red flags do not. Do not require anything the clause does not state.");
        sb.AppendLine();
        sb.AppendLine($"REGULATORY CLAUSE {clauseNo}:");
        sb.AppendLine(clauseText.Trim());
        sb.AppendLine();
        sb.AppendLine("GAP TO CHECK:");
        sb.AppendLine(gap.Line);
        sb.AppendLine();
        sb.AppendLine("--- INTERNAL DOCUMENT EXCERPTS ---");
        foreach (var e in excerpts)
        {
            sb.AppendLine($"[{e.Id}] {e.Label}");
            sb.AppendLine(e.Text.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("--- END EXCERPTS ---");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object (no markdown fences): {\"status\": \"covered\" | \"partial\" | \"not_covered\", \"evidence\": \"<excerpt id, e.g. E3, or empty>\", \"quote\": \"<the supporting text copied VERBATIM from that excerpt, or empty>\", \"reason\": \"<one sentence>\"}.");
        sb.AppendLine("covered = an excerpt fully meets the missing requirement.");
        sb.AppendLine("partial = an excerpt deals with the same subject and goes part of the way without fully meeting it: it applies the requirement in practice, covers some of its elements, or states a narrower version of it (for example a limited period, a single case, a related procedure). Quote the most relevant one; the gap stays and the quote is shown with it.");
        sb.AppendLine("not_covered = no excerpt deals with the subject of the missing requirement. For a missing definition: partial only when an excerpt states part of that definition; an excerpt that only uses the term is not_covered.");
        return sb.ToString();
    }

    /// <summary>Reads the answer; an unknown status counts as not covered.</summary>
    public static Answer ParseAnswer(string raw)
    {
        try
        {
            var answer = NdRegulLlmJsonHelper.ParseJsonObject<Answer>(raw);
            answer.Status = (answer.Status ?? "").Trim().ToLowerInvariant().Replace(' ', '_');
            if (answer.Status is not ("covered" or "partial" or "not_covered")) answer.Status = "not_covered";
            return answer;
        }
        catch (Exception)
        {
            return new Answer { Status = "not_covered", Reason = "unreadable answer" };
        }
    }

    /// <summary>The outcome for one gap: "covered" / "partial" only when the quote is verbatim in the named excerpt,
    /// otherwise "not_covered".</summary>
    public static Outcome Decide(Gap gap, Answer answer, IReadOnlyList<Excerpt> excerpts)
    {
        if (answer.Status == "not_covered")
            return new Outcome(gap, "not_covered", null, "", answer.Reason);
        var excerpt = excerpts.FirstOrDefault(e => string.Equals(e.Id, answer.Evidence?.Trim().Trim('[', ']'), StringComparison.OrdinalIgnoreCase));
        if (excerpt == null || !NdRegulJudgmentPostProcessor.VerifyQuote(answer.Quote ?? "", excerpt.Text))
            return new Outcome(gap, "not_covered", null, "", $"answer was '{answer.Status}' but its quote is not verbatim in the named excerpt");
        return new Outcome(gap, answer.Status, excerpt, answer.Quote!.Trim(), answer.Reason);
    }

    /// <summary>
    /// Rewrites the judgment: covered gaps become covered elements with their quote and reference; their action
    /// lines are removed; remaining gaps and actions are renumbered 1..n; partly covered gaps get a note; no gap
    /// left means compliant. Returns the judgment unchanged when nothing was covered or partly covered.
    /// </summary>
    public static RegulJudgmentResult Apply(RegulJudgmentResult judgment, IReadOnlyList<Outcome> outcomes)
    {
        var covered = outcomes.Where(o => o.Status == "covered" && o.Evidence != null).ToDictionary(o => o.Gap.Number);
        var partial = outcomes.Where(o => o.Status == "partial" && o.Evidence != null).ToDictionary(o => o.Gap.Number);
        if (covered.Count == 0 && partial.Count == 0) return judgment;

        var gaps = ParseGaps(judgment.GapDescription);
        var renumber = new Dictionary<int, int>();
        var keptGaps = new List<string>();
        foreach (var gap in gaps)
        {
            if (covered.ContainsKey(gap.Number)) continue;
            var next = keptGaps.Count + 1;
            renumber[gap.Number] = next;
            var line = gap.Line;
            if (partial.TryGetValue(gap.Number, out var p))
                line += $" Partly addressed: [{p.Evidence!.Label}] \"{p.Quote}\".";
            keptGaps.Add($"[{next}] {line}");
        }

        var keptActions = NumberedLines(judgment.SuggestedAction)
            .Where(a => renumber.ContainsKey(a.Number))
            .Select(a => $"[{renumber[a.Number]}] {a.Text}")
            .ToList();

        var coveredLines = NumberedLines(judgment.CoveredElements).Select(l => l.Text).ToList();
        if (coveredLines.Count == 0 && !string.IsNullOrWhiteSpace(judgment.CoveredElements)
            && !string.Equals(judgment.CoveredElements.Trim(), "None", StringComparison.OrdinalIgnoreCase))
            coveredLines.Add(judgment.CoveredElements.Trim());
        foreach (var c in covered.Values.OrderBy(c => c.Gap.Number))
            coveredLines.Add($"{c.Gap.Requirement} - Covered: [{c.Evidence!.Label}] (confirmed by the gap check)");

        var references = judgment.DocumentReference
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var quotes = judgment.PolicyExtract.ToList();
        foreach (var o in covered.Values.Concat(partial.Values))
        {
            if (!references.Contains(o.Evidence!.Label, StringComparer.OrdinalIgnoreCase)) references.Add(o.Evidence.Label);
            if (!quotes.Contains(o.Quote, StringComparer.Ordinal)) quotes.Add(o.Quote);
        }

        judgment.CoveredElements = coveredLines.Count == 0
            ? "None"
            : string.Join("\n", coveredLines.Select((l, i) => $"[{i + 1}] {l}"));
        judgment.PolicyExtract = quotes;
        judgment.DocumentReference = string.Join("; ", references);
        judgment.Interpretation = (judgment.Interpretation ?? "").TrimEnd()
            + $"\nGap check: {covered.Count} gap(s) found covered and {partial.Count} partly addressed in the documents after a wider search.";

        if (keptGaps.Count == 0)
        {
            judgment.OverallStatus = "compliant";
            judgment.DesignStatus = "compliant";
            judgment.OperatingStatus = "compliant";
            judgment.GapDescription = "N/A";
            judgment.SuggestedAction = "N/A";
            judgment.GapDirection = "";
        }
        else
        {
            judgment.GapDescription = string.Join("\n", keptGaps);
            judgment.SuggestedAction = keptActions.Count > 0 ? string.Join("\n", keptActions) : judgment.SuggestedAction;
        }

        return judgment;
    }
}
