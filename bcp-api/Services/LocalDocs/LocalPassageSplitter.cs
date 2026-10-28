using System.Text;
using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.LocalDocs;

public sealed record LocalPassage(
    int SectionIndex,
    int PassageIndex,
    string? ClauseNo,
    string HeadingPath,
    string Text,
    int? SourcePage);

/// <summary>
/// Cuts extracted sections into search passages of ~150-300 words, each with the heading path above it
/// (document title > parent headings > section heading > sub-headings inside the section). A whole section can
/// run to thousands of words (one annex of 19 typologies, a 4-page reporting chapter) while the embedding model
/// reads only the first ~380 words of what it is given, so retrieval pipeline v4+ searches these passages
/// instead. Nothing is rewritten or dropped: every line of every section is in at least one passage.
/// Sub-headings the numbering splitter keeps inside a section ("B.18 Other payment technologies", "Possible
/// indicators") start a new passage and extend the heading path.
/// </summary>
public static partial class LocalPassageSplitter
{
    /// <summary>A passage is closed at the first paragraph end after this many words.</summary>
    public const int TargetWords = 220;

    /// <summary>A passage never grows past this; a longer paragraph is cut at sentence ends.</summary>
    public const int MaxWords = 320;

    /// <summary>A sub-heading starts a new passage only when the open one already has this much text, so a
    /// heading followed by one line does not become a passage of its own.</summary>
    private const int MinWordsBeforeBreak = 60;

    /// <summary>An open passage with fewer words than this is only heading lines: a numbered sub-heading joins it
    /// (and renames it) instead of closing it as a passage of its own.</summary>
    private const int MinWordsForOwnPassage = 15;

    /// <summary>A last passage shorter than this joins the one before it (under the same heading).</summary>
    private const int MinTailWords = 40;

    /// <summary>The last sentence of a passage is repeated at the start of the next (when this short), so text
    /// that spans a cut is still found together.</summary>
    private const int MaxOverlapWords = 40;

    private const int MaxHeadingChars = 120;

    // "B.18 Other payment technologies", "18.2 How to Submit an STR", "3. Reporting": numbered heading lines.
    [GeneratedRegex(@"^(?:[A-Z]{1,3}\.)?\d{1,3}(?:\.\d{1,3})*\.?\s+[A-Z(]")]
    private static partial Regex NumberedSubHeading();

    [GeneratedRegex(@"^\s*(?:[•▪●◦‣∙·\-–*]|o\s)")]
    private static partial Regex BulletStart();

    [GeneratedRegex(@"(?<=[.;!?])\s+(?=[A-Z(""'])")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"^\d+(?:\.\d+)*$")]
    private static partial Regex DottedNumber();

    private sealed record Unit(string Text, int? Page, string? HeadingA, string? HeadingB, bool StartsHeading, bool IsNumberedHeading);

    public static IReadOnlyList<LocalPassage> Split(string documentTitle, IReadOnlyList<LocalSection> sections)
    {
        var headingByNo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sections)
        {
            if (string.IsNullOrWhiteSpace(s.ClauseNo) || headingByNo.ContainsKey(s.ClauseNo)) continue;
            headingByNo[s.ClauseNo] = HeadingLine(s);
        }

        var passages = new List<LocalPassage>();
        for (var i = 0; i < sections.Count; i++)
            passages.AddRange(SplitSection(documentTitle, sections[i], i, headingByNo));
        return passages;
    }

    private static IEnumerable<LocalPassage> SplitSection(
        string documentTitle, LocalSection section, int sectionIndex, IReadOnlyDictionary<string, string> headingByNo)
    {
        var basePath = BasePath(documentTitle, section, headingByNo);
        var units = Units(section).ToList();
        if (units.Count == 0) yield break;

        var drafts = new List<(string Path, StringBuilder Text, int Words, int? Page)>();
        var current = new StringBuilder();
        var currentWords = 0;
        int? currentPage = null;
        var currentPath = basePath;
        string? overlap = null;

        void Flush(bool carryOverlap)
        {
            if (currentWords == 0) return;
            var text = current.ToString().Trim();
            drafts.Add((currentPath, new StringBuilder(text), currentWords, currentPage));
            overlap = carryOverlap ? LastSentence(text) : null;
            if (overlap != null && WordCount(overlap) > MaxOverlapWords) overlap = null;
            current.Clear();
            currentWords = 0;
            currentPage = null;
        }

        void Append(string text, int? page)
        {
            if (currentWords == 0 && overlap != null)
            {
                current.Append(overlap).Append('\n');
                currentWords += WordCount(overlap);
                overlap = null;
            }

            current.Append(text).Append('\n');
            currentWords += WordCount(text);
            currentPage ??= page;
        }

        foreach (var unit in units)
        {
            var unitPath = JoinPath(basePath, unit.HeadingA, unit.HeadingB);
            // A numbered sub-heading ("B.18 ...") always starts a new passage, so its text is never searched under
            // the previous heading; a label ("Possible indicators") only when the open passage has enough text.
            if (unit.StartsHeading && currentWords > 0)
            {
                var minWords = unit.IsNumberedHeading ? MinWordsForOwnPassage : MinWordsBeforeBreak;
                if (currentWords >= minWords)
                {
                    Flush(carryOverlap: false);
                    overlap = null;
                }
                else if (unit.IsNumberedHeading)
                {
                    currentPath = unitPath; // the open passage holds only heading lines so far
                }
            }

            if (currentWords == 0) currentPath = unitPath;

            var words = WordCount(unit.Text);
            if (currentWords > 0 && currentWords + words > MaxWords && currentWords >= MinTailWords)
            {
                Flush(carryOverlap: true);
                currentPath = unitPath;
            }

            Append(unit.Text, unit.Page);
            if (currentWords >= TargetWords) Flush(carryOverlap: true);
        }

        Flush(carryOverlap: false);

        // A short tail joins the passage before it rather than being searched on its own.
        if (drafts.Count > 1 && drafts[^1].Words < MinTailWords && drafts[^1].Path == drafts[^2].Path
            && drafts[^2].Words + drafts[^1].Words <= MaxWords + MinTailWords)
        {
            var tail = drafts[^1];
            var prev = drafts[^2];
            drafts[^2] = (prev.Path, prev.Text.Append('\n').Append(tail.Text), prev.Words + tail.Words, prev.Page);
            drafts.RemoveAt(drafts.Count - 1);
        }

        for (var p = 0; p < drafts.Count; p++)
        {
            yield return new LocalPassage(
                sectionIndex,
                p,
                section.ClauseNo,
                drafts[p].Path,
                drafts[p].Text.ToString().Trim(),
                drafts[p].Page ?? section.SourcePage);
        }
    }

    /// <summary>Paragraph units with their page and the sub-heading they sit under. A paragraph longer than
    /// <see cref="MaxWords"/> is cut at sentence ends (and, failing that, by word count).</summary>
    private static IEnumerable<Unit> Units(LocalSection section)
    {
        string? headingA = null;
        string? headingB = null;
        var first = true;
        foreach (var (line, page) in Lines(section))
        {
            var isHeading = false;
            var isNumbered = false;
            if (first)
            {
                first = false; // the section's own heading line: already in the base path
            }
            else if (IsNumberedSubHeading(line))
            {
                headingA = Cap(line);
                headingB = null;
                isHeading = true;
                isNumbered = true;
            }
            else if (IsLabelHeading(line))
            {
                headingB = Cap(line);
                isHeading = true;
            }

            if (WordCount(line) <= MaxWords)
            {
                yield return new Unit(line, page, headingA, headingB, isHeading, isNumbered);
                continue;
            }

            foreach (var piece in CutLongParagraph(line))
                yield return new Unit(piece, page, headingA, headingB, false, false);
        }
    }

    private static IEnumerable<(string Line, int? Page)> Lines(LocalSection section)
    {
        if (section.PageBlocks is { Count: > 0 } blocks)
        {
            foreach (var block in blocks)
                foreach (var line in block.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
                    yield return (line, block.Page);
            yield break;
        }

        foreach (var line in section.ClauseText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            yield return (line, section.SourcePage);
    }

    private static IEnumerable<string> CutLongParagraph(string paragraph)
    {
        var group = new StringBuilder();
        var groupWords = 0;
        foreach (var sentence in SentenceEnd().Split(paragraph).Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            var words = WordCount(sentence);
            if (words > MaxWords)
            {
                if (groupWords > 0) { yield return group.ToString(); group.Clear(); groupWords = 0; }
                var tokens = sentence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < tokens.Length; i += TargetWords)
                    yield return string.Join(' ', tokens.Skip(i).Take(TargetWords));
                continue;
            }

            if (groupWords > 0 && groupWords + words > TargetWords)
            {
                yield return group.ToString();
                group.Clear();
                groupWords = 0;
            }

            if (groupWords > 0) group.Append(' ');
            group.Append(sentence);
            groupWords += words;
        }

        if (groupWords > 0) yield return group.ToString();
    }

    private static bool IsNumberedSubHeading(string line) =>
        line.Length <= 100 && !EndsSentence(line) && !line.Contains(" | ", StringComparison.Ordinal)
        && !line.Contains(';') && !EndsWithConjunction(line) && NumberedSubHeading().IsMatch(line);

    // "3. A financial institution; or" / "... and": a list item continuing into the next line, not a heading.
    private static bool EndsWithConjunction(string line) =>
        line.EndsWith(" or", StringComparison.OrdinalIgnoreCase) || line.EndsWith(" and", StringComparison.OrdinalIgnoreCase);

    /// <summary>A short title line ("Possible indicators", "Placement"): 1-6 words, capitalised, no sentence
    /// punctuation, not a bullet or a table row.</summary>
    private static bool IsLabelHeading(string line)
    {
        if (line.Length is < 4 or > 60 || EndsSentence(line) || line.Contains(" | ", StringComparison.Ordinal)) return false;
        if (line.Contains(';') || line.Contains(',') || EndsWithConjunction(line)) return false;
        if (BulletStart().IsMatch(line) || !char.IsUpper(line[0])) return false;
        var words = WordCount(line);
        return words is >= 1 and <= 6;
    }

    private static bool EndsSentence(string line)
    {
        var t = line.TrimEnd().TrimEnd('"', '\'', ')', ']');
        return t.Length == 0 || t[^1] is '.' or ';' or ':' or ',' or '!' or '?';
    }

    private static string BasePath(string documentTitle, LocalSection section, IReadOnlyDictionary<string, string> headingByNo)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(documentTitle)) parts.Add(Cap(documentTitle.Trim()));

        var no = section.ClauseNo?.Trim() ?? "";
        if (DottedNumber().IsMatch(no))
        {
            var segments = no.Split('.');
            for (var i = 1; i < segments.Length; i++)
            {
                var ancestor = string.Join('.', segments.Take(i));
                if (headingByNo.TryGetValue(ancestor, out var heading)) parts.Add(heading);
            }
        }

        parts.Add(HeadingLine(section));
        return string.Join(" > ", parts.Where(p => p.Length > 0));
    }

    private static string JoinPath(string basePath, string? headingA, string? headingB)
    {
        var path = basePath;
        if (!string.IsNullOrWhiteSpace(headingA)) path += " > " + headingA;
        if (!string.IsNullOrWhiteSpace(headingB)) path += " > " + headingB;
        return path;
    }

    private static string HeadingLine(LocalSection section)
    {
        var first = section.ClauseText.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return Cap(first.Length > 0 ? first : section.ClauseNo ?? "");
    }

    private static string Cap(string text) =>
        text.Length > MaxHeadingChars ? text[..MaxHeadingChars].TrimEnd() + "..." : text;

    private static string? LastSentence(string text)
    {
        var last = SentenceEnd().Split(text.Replace('\n', ' ')).Select(s => s.Trim()).LastOrDefault(s => s.Length > 0);
        return string.IsNullOrWhiteSpace(last) ? null : last;
    }

    public static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
