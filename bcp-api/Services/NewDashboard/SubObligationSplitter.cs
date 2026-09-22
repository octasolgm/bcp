using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Hybrid pipeline Step 2 (V5 / RegulPipelineHybrid engine only, gov side only — never reads
/// internal documents): a single gov clause sometimes bundles more than one distinct obligation
/// ("LFIs must do X and must report Y within 5 days" is really two separate requirements). Free,
/// local, regex-only — no AI call — mirroring the same "no AI needed for something this
/// mechanical" reasoning as <see cref="LocalDocs.LocalSectionSplitter"/>.
///
/// Splitting happens once per clause, right after Step 1 (query expansion) and before Step 3/4
/// retrieval — each sub-obligation gets its own BM25 + embedding search, since a bundled clause
/// searched as one blended query can wash out a section that only matches one of its several
/// obligations. Deliberately conservative: when nothing confidently indicates more than one
/// obligation, the clause is returned unsplit (a single "sub-obligation" that just is the whole
/// clause) rather than risk fragmenting a clause that reads fine as one unit.
/// </summary>
public static partial class SubObligationSplitter
{
    private const int MinPartLength = 20;
    private const int MaxParts = 8;

    // Lettered/numbered/roman sub-items at the start of a line or right after a colon/semicolon
    // ("(a) ...", "(b) ...", "(i) ...", "1) ..."), OR a bullet character at the start of a line
    // ("· LFIs should ...", "• ..."). Real regulatory documents (confirmed against TFS Guidelines
    // v12, e.g. clause 2.5 "Policies and Procedures") use plain bullets for enumerated obligation
    // lists at least as often as lettered parens — both are equally reliable signals that a
    // clause is really a list of separate obligations rather than one sentence that's just long.
    [GeneratedRegex(@"(?:(?:(?<=^)|(?<=[:;]\s))\(?(?:[a-z]|[ivx]{1,4}|\d{1,2})\)[\.\s]+)|(?:^[ \t]*[·•]\s+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex EnumeratedItem();

    // Same obligation-modal vocabulary the frontend's gov-point-filter.ts uses to classify a
    // point as a real requirement (kept in sync deliberately — see that file's own comment).
    [GeneratedRegex(@"\b(must|shall|should|required to|obliged to|ensure that|are required|need to|have to)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ObligationModal();

    // Sentence boundary: end punctuation followed by whitespace and a capital letter or opening
    // paren — avoids splitting on abbreviation periods mid-sentence in the common case.
    [GeneratedRegex(@"(?<=[.;])\s+(?=[A-Z(])")]
    private static partial Regex SentenceBoundary();

    /// <summary>Splits one gov clause into its distinct obligations. Always returns at least one
    /// entry (the original text) — callers don't need a separate "was it split" check.</summary>
    public static IReadOnlyList<string> Split(string clauseText)
    {
        var text = (clauseText ?? "").Trim();
        if (text.Length == 0) return [text];

        var enumerated = SplitByEnumeratedItems(text);
        if (enumerated is { Count: > 1 }) return enumerated;

        var bySentence = SplitByObligationSentences(text);
        if (bySentence is { Count: > 1 }) return bySentence;

        return [text];
    }

    private static List<string>? SplitByEnumeratedItems(string text)
    {
        var pieces = EnumeratedItem().Split(text)
            .Select(p => p.Trim())
            .Where(p => p.Length >= MinPartLength)
            .ToList();
        if (pieces.Count < 2) return null;

        // The text before the first match (the shared stem introducing the list, e.g. "LFIs must:")
        // carries context every item needs — prepend it to each item rather than discarding it.
        var firstMatch = EnumeratedItem().Match(text);
        var stem = firstMatch.Success ? text[..firstMatch.Index].Trim() : "";
        var withStem = stem.Length >= MinPartLength
            ? pieces.Select(p => $"{stem} {p}").ToList()
            : pieces;

        return withStem.Count > MaxParts ? withStem[..MaxParts] : withStem;
    }

    private static List<string>? SplitByObligationSentences(string text)
    {
        var sentences = SentenceBoundary().Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length >= MinPartLength)
            .ToList();
        if (sentences.Count < 2) return null;

        var withObligation = sentences.Where(s => ObligationModal().IsMatch(s)).ToList();
        // Only worth splitting when there's more than one independently-obligating sentence —
        // one obligation sentence plus supporting/definitional sentences is still one obligation.
        if (withObligation.Count < 2) return null;

        return withObligation.Count > MaxParts ? withObligation[..MaxParts] : withObligation;
    }
}
