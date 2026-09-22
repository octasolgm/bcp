using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.LocalDocs;

public sealed record LocalSection(string ClauseNo, string ClauseText, int? SourcePage);

/// <summary>
/// Local, offline clause/section detection — regex over numbering conventions regulation and policy
/// documents already use consistently ("6.2", "9.4.1", "Article 12", "Rule 9.4.1"), no AI involved.
/// This is the part of Landing AI's "extract" step that's genuinely risky to replace — validate
/// against a golden set of already-extracted documents before trusting this on real analysis runs
/// (see docs/discussion/REGUL-PIPELINE-BUILD-PLAN.md, Task 2.4).
/// </summary>
public static partial class LocalSectionSplitter
{
    // Numbered heading: "9.4.1 Independent audit", "6.2. Title", up to 4 levels deep. Title must start
    // with a CAPITAL letter — a real heading title always does. Without this, a line-wrapped in-sentence
    // reference like "Cabinet Decision No.\n74 of 2020 or failing to implement..." (the number lands at
    // the start of a wrapped line purely because of where the PDF happened to break) matches just as
    // well as a real heading, and — like the earlier footnote/Article-reference bugs — hijacks the rest
    // of the page into a fake clause. Confirmed on a real document: this exact pattern created a bogus
    // clause "74" that swallowed real content belonging to clause 7.6.
    [GeneratedRegex(@"^(?<no>\d{1,3}(\.\d{1,3}){0,4})\.?\s+(?<title>[A-Z].{2,})$")]
    private static partial Regex NumberedHeading();

    // Labelled heading: "Article 12", "Section 6.2", "Rule 9.4.1", "Clause 3".
    [GeneratedRegex(@"^(?<label>Article|Section|Rule|Clause|Chapter|Annex)\s+(?<no>\d{1,3}(\.\d{1,3}){0,4})\b\.?\s*(?<title>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LabelledHeading();

    // A footnote marker ("3 Website: Home | Committee...", "1 Available at https://...") is a bare
    // top-level number followed by a citation-style phrase or a URL — it happens to satisfy
    // NumberedHeading's shape ("number. Capitalized text") too, which was confirmed to actively corrupt
    // real clause data: matching it starts a fake new "clause" that then swallows every line after it
    // (up to the next real heading) — including the real continuation of whatever clause the footnote
    // was actually attached to (e.g. clause 1.4's Definitions list got cut off mid-list because footnote
    // "3" hijacked the rest of the page). Never treat these as headings — let them fall through as
    // ordinary text appended to whichever clause is currently open, where they topically belong anyway.
    // "As of <date>," is another common footnote lead-in ("1 As of October 18, 2023, the targeted
    // financial sanctions..."), confirmed on a real document to hijack a large stretch of the following
    // clause's real content into a bogus clause "1" — same failure mode as the other footnote patterns.
    [GeneratedRegex(@"^\d{1,3}\s+(?:Available\s+at|Website\s*:|See\s|https?://|As\s+of\s)", RegexOptions.IgnoreCase)]
    private static partial Regex FootnoteMarker();

    private const int MinHeadingLineLength = 3;
    private const int MaxHeadingLineLength = 160;

    // "Article 44.11 of the Cabinet Decision No. (10) of 2019 Concerning..." is a normal in-sentence
    // reference inside a clause's body, not a heading — but it satisfies LabelledHeading's shape just
    // like a real "Article 12" heading does, and matching it hijacks the rest of the page the same way
    // the footnote-marker bug did. A real Article/Section/Annex heading is always a short title (see
    // every real one in this codebase's test documents: well under 60 characters); a body-text reference
    // to one is part of a full sentence and reliably runs much longer. Cap LabelledHeading to a shorter
    // length than NumberedHeading (whose titles can legitimately run longer) to tell the two apart.
    private const int MaxLabelledHeadingLength = 80;

    // Azure's markdown output wraps headings in bold ("**3.4. Name Screening**"), turns references into
    // links ("[UNSC website](https://...)"), and can prefix real headings with "#"/"##". Matching and
    // storing raw markdown against that produces two real, reported symptoms: a bold-wrapped heading line
    // fails NumberedHeading/LabelledHeading (which require the WHOLE line to be plain "number. Title"),
    // so it silently merges into the previous clause instead of starting a new one; and un-stripped link/
    // bold syntax ends up verbatim inside stored clause text. Clean every line the same way before either
    // check, so a document with heavy markdown formatting behaves the same as plain OCR text would.
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkPattern = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownBoldPattern = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex MarkdownItalicPattern = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex MarkdownHeadingPrefixPattern = new(@"^#{1,6}\s*", RegexOptions.Compiled);

    private static string CleanMarkdown(string line)
    {
        var cleaned = HtmlTagPattern.Replace(line, "");
        cleaned = MarkdownLinkPattern.Replace(cleaned, "$1 ($2)");
        cleaned = MarkdownBoldPattern.Replace(cleaned, "$1");
        cleaned = MarkdownItalicPattern.Replace(cleaned, "$1");
        cleaned = MarkdownHeadingPrefixPattern.Replace(cleaned, "");
        return cleaned.Trim();
    }

    public static IReadOnlyList<LocalSection> Split(IReadOnlyList<LocalPageResult> pages)
    {
        var sections = new List<LocalSection>();
        string? currentNo = null;
        var currentText = new System.Text.StringBuilder();
        int? currentPage = null;

        // An Annex commonly restarts its own "1., 2., 3." numbering for a red-flag/checklist-style
        // sub-list — those numbers collide with the document's real top-level clauses of the same
        // number (e.g. this document's Annex 1 has its own "2. Red Flag Indicators for PF", which is
        // NOT the same thing as the real top-level "2. SANCTIONS COMPLIANCE PROGRAM"). Anything keying
        // off ClauseNo downstream (gap analysis grouping, point lookup) can't tell the two apart unless
        // the Annex's own numbering is namespaced under it.
        string? currentAnnexLabel = null;

        void Flush()
        {
            if (currentNo == null) return;
            var text = currentText.ToString().Trim();
            if (text.Length > 0)
                sections.Add(new LocalSection(currentNo, text, currentPage));
            currentText.Clear();
        }

        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page.Text)) continue;

            var pageLines = ContentLines(page.Text);

            // A page that is a plain-text table of contents ("3.1 Title" / "13" / "3.2 Title" / "14" ...)
            // holds no clause prose — every line on it is a pointer to a clause that appears later. Read as
            // clauses, each entry duplicates a real clause number, and the last entry also swallows whatever
            // follows it (the next contents line, the running page header, ...).
            if (LooksLikeContentsPage(pageLines)) continue;

            foreach (var line in pageLines)
            {
                var matched = TryMatchHeading(line);
                if (matched != null)
                {
                    var (headingNo, isLabelled) = matched.Value;
                    Flush();
                    if (isLabelled && headingNo.StartsWith("Annex ", StringComparison.OrdinalIgnoreCase))
                        currentAnnexLabel = headingNo;
                    else if (currentAnnexLabel != null && !isLabelled)
                        headingNo = $"{currentAnnexLabel}.{headingNo}";
                    currentNo = headingNo;
                    currentPage = page.PageNumber;
                    currentText.AppendLine(line);
                    continue;
                }

                // Text before the first detected heading — keep as an "Introduction" bucket rather than dropping it,
                // so nothing from the document is silently lost even if numbering hasn't started yet.
                currentNo ??= "Introduction";
                currentPage ??= page.PageNumber;
                currentText.AppendLine(line);
            }
        }

        Flush();
        return MergeRepeatedTopLevelNumbers(DropTableOfContentsDuplicates(sections, pages));
    }

    // Tables (e.g. this document's own table of contents) are structural data, not clause prose
    // — skip them entirely rather than let stray <td>/<tr> fragments leak into whichever clause
    // happens to be open when the table appears.
    private static List<string> ContentLines(string pageText)
    {
        var lines = new List<string>();
        var inTable = false;
        foreach (var rawLine in pageText.Split('\n'))
        {
            if (rawLine.Contains("<table", StringComparison.OrdinalIgnoreCase)) { inTable = true; continue; }
            if (rawLine.Contains("</table", StringComparison.OrdinalIgnoreCase)) { inTable = false; continue; }
            if (inTable) continue;

            var line = CleanMarkdown(rawLine);
            if (line.Length > 0) lines.Add(line);
        }

        return lines;
    }

    // A footnote printed at the bottom of a page ("1 Social Media and Terrorism Financing: A joint project by
    // ...") looks like a top-level numbered heading. It opens a fake clause "1" that swallows the rest of the
    // page — including the end of the sentence the footnote interrupted, which is cut off in the clause it
    // belongs to. The same happens to a numbered list inside a clause ("1. Report to the FIU", "2. Keep
    // records") once the chapter numbers 1, 2, ... have already been used.
    //
    // Clause numbers are unique in a document, so a plain top-level number that has already been used and is
    // NOT followed by its own sub-clauses ("1" then "1.1") cannot be a real chapter: fold its text back into
    // the clause that was open when it appeared, restoring the original reading order. A genuine chapter is
    // always followed by its own sub-clauses, so it is never merged.
    private static readonly Regex BareTopLevelNumber = new(@"^\d{1,3}$", RegexOptions.Compiled);

    private static IReadOnlyList<LocalSection> MergeRepeatedTopLevelNumbers(IReadOnlyList<LocalSection> sections)
    {
        var merged = new List<LocalSection>(sections.Count);
        var seen = new HashSet<string>();
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            if (merged.Count > 0
                && BareTopLevelNumber.IsMatch(section.ClauseNo)
                && seen.Contains(section.ClauseNo))
            {
                var next = i + 1 < sections.Count ? sections[i + 1].ClauseNo : null;
                var hasOwnSubClauses = next != null
                    && next.StartsWith(section.ClauseNo + ".", StringComparison.Ordinal);
                if (!hasOwnSubClauses)
                {
                    var previous = merged[^1];
                    merged[^1] = previous with { ClauseText = previous.ClauseText.TrimEnd() + "\n" + section.ClauseText };
                    continue;
                }
            }

            seen.Add(section.ClauseNo);
            merged.Add(section);
        }

        return merged;
    }

    private const int MinContentsEntriesPerPage = 4;

    private static bool LooksLikeContentsPage(IReadOnlyList<string> lines)
    {
        var entries = 0;
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (PageNumberOnlyLine.IsMatch(lines[i + 1]) && TryMatchHeading(lines[i]) != null)
                entries++;
        }

        return entries >= MinContentsEntriesPerPage;
    }

    // A table of contents printed as plain text (not an HTML table) is extracted like any other page:
    // "3.1 Summary of Minimum Statutory Obligations" followed by the bare page number "13" becomes a clause
    // "3.1" whose whole body is a page number. The real clause 3.1 appears later, so every clause listed
    // in the contents exists twice with the same number — downstream (point ids, the analysis point
    // picker, per-clause LLM calls) then collides on it and either loses the real clause or spends an
    // analysis call on the table-of-contents line. Drop the page-number-only copy, but only when a real
    // clause with the same number exists, so a genuinely heading-only clause is never lost.
    private static readonly Regex PageNumberOnlyLine =
        new(@"^(?:\d{1,4}|[ivxlcdm]{1,6})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Lines that repeat on several pages are the running page header / footer ("Anti-Money Laundering ...
    // Guidelines for Financial Institutions"), not clause text. Ignored when judging whether an entry is
    // only a page number, so a contents entry that ends a page is not "rescued" by the header printed under it.
    private const int RunningLineMinPages = 3;

    private static HashSet<string> RunningLines(IReadOnlyList<LocalPageResult> pages) =>
        pages
            .SelectMany(p => ContentLines(p.Text ?? "").Where(l => l.Length >= 12).Distinct().Select(l => (Line: l, p.PageNumber)))
            .GroupBy(x => x.Line)
            .Where(g => g.Select(x => x.PageNumber).Distinct().Count() >= RunningLineMinPages)
            .Select(g => g.Key)
            .ToHashSet();

    private static bool IsPageNumberOnlyBody(string clauseText, HashSet<string> runningLines)
    {
        var lines = clauseText.Split('\n').Select(l => CleanMarkdown(l)).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return false;
        var body = lines.Skip(1).Where(l => !runningLines.Contains(l)).ToList();
        return body.All(l => PageNumberOnlyLine.IsMatch(l));
    }

    private static IReadOnlyList<LocalSection> DropTableOfContentsDuplicates(
        List<LocalSection> sections,
        IReadOnlyList<LocalPageResult> pages)
    {
        var runningLines = RunningLines(pages);
        var hasRealClause = sections
            .Where(s => !IsPageNumberOnlyBody(s.ClauseText, runningLines))
            .Select(s => s.ClauseNo)
            .ToHashSet();
        return sections
            .Where(s => !IsPageNumberOnlyBody(s.ClauseText, runningLines) || !hasRealClause.Contains(s.ClauseNo))
            .ToList();
    }

    private static (string No, bool IsLabelled)? TryMatchHeading(string line)
    {
        if (line.Length is < MinHeadingLineLength or > MaxHeadingLineLength) return null;
        if (FootnoteMarker().IsMatch(line)) return null;

        var numbered = NumberedHeading().Match(line);
        if (numbered.Success) return (numbered.Groups["no"].Value, false);

        var labelled = LabelledHeading().Match(line);
        if (labelled.Success
            && line.Length <= MaxLabelledHeadingLength
            && LooksLikeLabelledHeadingTitle(labelled.Groups["title"].Value))
            return ($"{labelled.Groups["label"].Value} {labelled.Groups["no"].Value}", true);

        return null;
    }

    // A wrapped in-sentence reference can put "Article 4.1);" or "Article 21, 44.12);" at the start of a
    // line ("...AML-CFT Decision" / "Article 4.1);"). It is short, so the length cap does not catch it, and
    // it matched LabelledHeading — closing the real clause (3.1 was cut off mid-bullet-list) and opening a
    // fake "Article 4.1" clause. A real heading is either the bare label ("Article 12") or is followed by
    // a title that starts with a capital letter, optionally after a ":" / dash. Anything else — a closing
    // bracket, ";", ",", "(a)", or a lower-case "of the ..." — is a reference, not a heading.
    private static bool LooksLikeLabelledHeadingTitle(string title)
    {
        title = title.Trim().TrimStart(':', '-', '–', '—').Trim();
        return title.Length == 0 || char.IsUpper(title[0]);
    }
}
