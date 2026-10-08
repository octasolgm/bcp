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

    /// <summary>Retrieval pipeline v3+ uses <see cref="SplitComplete"/>; earlier versions keep the original
    /// split so their runs stay reproducible.</summary>
    public static IReadOnlyList<string> Split(string clauseText, int pipelineVersion) =>
        pipelineVersion >= NdRegulPipelineVersions.V3RelevanceSelection ? SplitComplete(clauseText) : Split(clauseText);

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

    // ---------------------------------------------------------------- pipeline v3

    // v3 never drops clause text: every paragraph and every list item becomes a piece, with no limit on the
    // number of pieces (v1/v2 kept only the first 8, which left the end of long clauses unsearched, e.g. the
    // "size / timeframe / nature of funds are irrelevant" paragraph of AML-CFT Guidelines 3.5).

    /// <summary>A paragraph longer than this is searched as groups of sentences, so one long paragraph does
    /// not blur into a single vague query. Nothing is dropped; the groups together are the whole paragraph.</summary>
    private const int MaxParagraphPieceChars = 700;
    private const int SentenceGroupChars = 450;
    /// <summary>The list lead-in ("... defines money laundering as any of the following:") is repeated in
    /// front of each item so the item keeps its meaning; a very long lead-in is cut to its end.</summary>
    private const int MaxLeadInChars = 300;

    // A list item at the start of a line: bullet characters, a dash, an OCR-read bullet (". Currency
    // smuggling"), (a) / a) / (iv) / 1) items, or "1." items.
    [GeneratedRegex(@"^(?:[·•▪●◦‣∙]|[-–*](?=\s)|\.(?=\s)|\(?(?:[a-z]|[ivx]{1,4}|\d{1,2})\)|\d{1,2}\.(?=\s))\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ListMarker();

    // Inline items after a colon or semicolon ("LFIs must: (a) do X; (b) do Y") are moved to their own line.
    [GeneratedRegex(@"(?<=[:;])[ \t]+(?=\(?(?:[a-z]|[ivx]{1,4}|\d{1,2})\)\s)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineEnumerator();

    [GeneratedRegex(@"(?<=[.;!?])\s+(?=[A-Z(""])")]
    private static partial Regex SentenceEnd();

    // Lead-in boundary: also after a closing bracket, so "3.5 Money Laundering (AML-CFT Law Articles ...) The AML-CFT
    // Law defines ..." gives the lead-in "The AML-CFT Law defines ...", without the heading and citation.
    [GeneratedRegex(@"(?<=[.;!?)])\s+(?=[A-Z(""])")]
    private static partial Regex LeadInStart();

    private sealed record Block(bool IsItem, string Text);

    /// <summary>
    /// Pipeline v3 split: the whole clause, as pieces. Paragraphs are pieces (long ones as sentence groups),
    /// every list item is a piece with its list lead-in in front, and a piece too short to search on its own
    /// is merged into its neighbour. Every sentence of the clause is in exactly one piece (plus the lead-in
    /// repeated on its items). Returns the clause unchanged as one entry when it is a single short paragraph.
    /// </summary>
    public static IReadOnlyList<string> SplitComplete(string clauseText)
    {
        var text = (clauseText ?? "").Trim();
        if (text.Length == 0) return [text];

        var normalized = InlineEnumerator().Replace(text.Replace("\r\n", "\n"), "\n");
        var pieces = new List<string>();
        string? leadIn = null;
        foreach (var block in BuildBlocks(normalized))
        {
            if (block.IsItem)
            {
                pieces.Add(leadIn == null ? block.Text : $"{leadIn} {block.Text}");
                continue;
            }

            pieces.AddRange(SplitLongParagraph(block.Text));
            leadIn = TrimClosers(block.Text).EndsWith(':') ? LastSentence(block.Text) : null;
        }

        pieces = MergeShortPieces(pieces);
        return pieces.Count <= 1 ? [text] : pieces;
    }

    private static List<Block> BuildBlocks(string text)
    {
        var blocks = new List<Block>();
        var current = new System.Text.StringBuilder();
        var currentIsItem = false;

        void Flush()
        {
            var t = current.ToString().Trim();
            if (t.Length > 0) blocks.Add(new Block(currentIsItem, t));
            current.Clear();
        }

        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            // The first line is the clause's own heading ("3. Highlights ..."), never a list item.
            var marker = i == 0 ? Match.Empty : ListMarker().Match(line);
            if (marker.Success && marker.Length > 0 && marker.Length < line.Length)
            {
                Flush();
                currentIsItem = true;
                current.Append(line[marker.Length..].Trim());
                continue;
            }

            // A line continues the open item / paragraph unless that one already ended a sentence: a wrapped
            // line ("or disguising their Illegal source;") belongs to its item, "Both the AML-CFT Law ..."
            // after "... to escape punishment." starts a new paragraph.
            if (current.Length > 0 && !EndsSentence(current.ToString()))
            {
                current.Append(' ').Append(line);
                continue;
            }

            Flush();
            currentIsItem = false;
            current.Append(line);
        }

        Flush();
        return blocks;
    }

    private static string TrimClosers(string text) => text.TrimEnd().TrimEnd('"', '\'', '\u201D', '\u2019', ')', ']');

    private static bool EndsSentence(string text)
    {
        var t = TrimClosers(text);
        return t.Length > 0 && t[^1] is '.' or ':' or ';' or '!' or '?';
    }

    private static IEnumerable<string> SplitLongParagraph(string paragraph)
    {
        if (paragraph.Length <= MaxParagraphPieceChars)
        {
            yield return paragraph;
            yield break;
        }

        var group = new System.Text.StringBuilder();
        foreach (var sentence in SentenceEnd().Split(paragraph).Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            if (group.Length > 0 && group.Length + 1 + sentence.Length > SentenceGroupChars)
            {
                yield return group.ToString();
                group.Clear();
            }

            if (group.Length > 0) group.Append(' ');
            group.Append(sentence);
        }

        if (group.Length > 0) yield return group.ToString();
    }

    private static string LastSentence(string paragraph)
    {
        var last = LeadInStart().Split(paragraph).Select(s => s.Trim()).LastOrDefault(s => s.Length > 0) ?? paragraph;
        if (last.Length <= MaxLeadInChars) return last;
        var tail = last[^MaxLeadInChars..];
        var space = tail.IndexOf(' ');
        return space > 0 ? tail[(space + 1)..] : tail;
    }

    private static List<string> MergeShortPieces(List<string> pieces)
    {
        var merged = new List<string>();
        string? carry = null;
        foreach (var piece in pieces)
        {
            var current = carry == null ? piece : $"{carry} {piece}";
            carry = null;
            if (current.Length >= MinPartLength)
            {
                merged.Add(current);
            }
            else if (merged.Count > 0)
            {
                merged[^1] = $"{merged[^1]} {current}";
            }
            else
            {
                carry = current; // first piece too short: goes in front of the next one
            }
        }

        if (carry != null) merged.Add(carry);
        return merged;
    }
}
