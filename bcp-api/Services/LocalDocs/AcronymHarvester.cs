using System.Text.RegularExpressions;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.LocalDocs;

public sealed record HarvestedAcronym(string Acronym, string Definition, int? SourcePage);
public sealed record CandidateAcronym(string Acronym, int? SourcePage);

/// <summary>
/// Auto-harvests acronym/full-form pairs from already-parsed document text for query expansion
/// (hybrid pipeline Step 1 — see docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md). Same
/// philosophy as <see cref="LocalSectionSplitter"/>: check whether text *looks like* a known
/// shape, never read for meaning. The shape here is a regulatory-writing convention — a term is
/// almost always spelled out in full the first time it's used, immediately followed by its
/// short form in parentheses: <c>"Targeted Financial Sanctions (TFS)"</c>.
/// </summary>
public static partial class AcronymHarvester
{
    // "Full Form Phrase (ABBR)" — every word must itself be capitalized (title case), with a
    // short, fixed list of lowercase connector words ("of", "the", "and"...) as the ONLY
    // exception. This must be `[A-Z]`, not `[A-Za-z]`, for the generic word branch — using
    // `[A-Za-z]` here was a confirmed real bug: it silently accepted ANY lowercase word (not
    // just the intended connector list), which let the match start too early and swallow
    // unrelated preceding text — e.g. "In addition, the Financial Action Task Force (FATF)"
    // captured "In addition, the Financial Action Task Force" as the "definition" instead of
    // just "Financial Action Task Force", because "addition," slipped through as a generic
    // lowercase word. Regex scanning tries the leftmost valid start first, so once a lowercase
    // non-connector word is wrongly allowed mid-phrase, the match starts as early as the first
    // capitalized word anywhere before it, however unrelated. Requiring `[A-Z]` for every word
    // except the named connectors makes an invalid start fail fast, so the engine falls through
    // to the next (correct, closer-to-the-parenthesis) candidate start instead.
    [GeneratedRegex(@"(?<def>[A-Z][A-Za-z][\w&/,'-]*(?:\s+(?:[A-Z][\w&/,'-]*|of|the|and|for|on|in)){1,8})\s\((?<acr>[A-Z]{2,8})\)")]
    private static partial Regex DefinitionThenAcronym();

    // A bare short-form token used somewhere in ordinary prose — e.g. "...under the SCP
    // framework..." — candidate for "used but never spelled out anywhere". Deliberately narrower
    // than the definition regex above (2-6 letters, optional trailing plural 's') to keep noise
    // manageable; admin can deactivate any false positive from the admin page.
    [GeneratedRegex(@"\b(?<acr>[A-Z]{2,6})s?\b")]
    private static partial Regex StandaloneToken();

    // Roman numerals ("II", "III", "IV"...) satisfy the {2,8} all-caps shape but are never real
    // acronyms — a document numbering a list item "Weapons of Mass Destruction (WMD)II" style
    // reference, or a plain roman-numeral cross-reference, would otherwise pollute the
    // dictionary. Reject the whole match rather than guess.
    private static readonly Regex RomanNumeral = new(@"^M{0,4}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$", RegexOptions.Compiled);

    // Common function/grammar words that satisfy the bare-token shape (2-6 all-caps letters) but
    // are never real domain acronyms — a document that happens to render them in caps somewhere
    // (a table cell, a stray formatting artifact) would otherwise flood the "unresolved" list with
    // obvious junk before an admin ever sees it. Content-word false positives (a real noun caught
    // in caps for some other reason) are left to admin curation on the dictionary page instead —
    // this stoplist only covers words no compliance acronym could ever legitimately be.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // Grammar/function words — could never be an acronym.
        "AND", "THE", "FOR", "NOT", "ARE", "WAS", "HAS", "ALL", "ANY", "NEW", "CAN", "ITS",
        "WHO", "MAY", "PER", "VIA", "TO", "OF", "IN", "ON", "AT", "BY", "OR", "IF", "SO", "NO",
        "NOR", "BUT", "YET", "THIS", "THAT", "WITH", "FROM", "INTO", "ONTO", "OVER", "UNDER",
        "THEN", "THAN", "WHEN", "WHERE", "WHICH", "WHILE", "THEIR", "THERE", "THESE", "THOSE",
        "SUCH", "EACH", "BOTH", "MORE", "MOST", "SOME", "ONLY", "ALSO", "EVEN", "STILL", "WILL",
        "SHALL", "MUST", "WOULD", "COULD", "SHOULD",
        // Ordinary compliance-document vocabulary that regularly gets rendered in caps for
        // emphasis (a bolded term, a heading fragment PDF/OCR flattens to literal capitals) —
        // real acronyms are never these words, so flagging them as "unresolved" just floods the
        // dictionary page with noise an admin has to individually deactivate. Reported false
        // positives: NON, FRAUD, SOCIAL.
        "NON", "FRAUD", "SOCIAL", "MEDIA", "RISK", "RISKS", "POLICY", "POLICIES", "BOARD",
        "STAFF", "AUDIT", "REPORT", "REPORTS", "CUSTOMER", "CUSTOMERS", "INTERNAL", "EXTERNAL",
        "CONTROL", "CONTROLS", "PROCESS", "PROCESSES", "REVIEW", "ANNUAL", "BUSINESS", "SYSTEM",
        "SYSTEMS", "GROUP", "TEAM", "LEVEL", "LEVELS", "AREA", "AREAS", "ISSUE", "ISSUES",
        "ACTION", "ACTIONS", "PLAN", "PLANS", "RULE", "RULES", "LAW", "LAWS", "ACT", "ACTS",
        "ORDER", "ORDERS", "NOTICE", "NOTICES", "FORM", "FORMS", "DATA", "SECTION", "SECTIONS",
        "ARTICLE", "ARTICLES", "CLAUSE", "CLAUSES", "PUBLIC", "PRIVATE", "GENERAL", "DIRECT",
        "SENIOR", "GLOBAL", "LOCAL", "NATIONAL", "FEDERAL", "STATE", "CENTRAL", "MAIN", "FINAL",
        "RECORD", "RECORDS", "ACCOUNT", "ACCOUNTS", "DOCUMENT", "DOCUMENTS", "TRAINING",
        "OFFICER", "OFFICERS", "COMMITTEE", "DEPARTMENT", "STANDARD", "STANDARDS", "GUIDANCE",
        "GUIDELINE", "GUIDELINES", "FRAMEWORK", "PROCEDURE", "PROCEDURES",
    };

    private const int MinDefinitionWords = 2;
    private const int MaxDefinitionLength = 90;

    /// <summary>Real "Full Form (ABBR)" pairs — both fields known.</summary>
    public static IReadOnlyList<HarvestedAcronym> Harvest(string markdownText) =>
        WalkPages<HarvestedAcronym>(markdownText, HarvestFromText);

    /// <summary>Bare short-form tokens used somewhere in the text (e.g. "the SCP") — no
    /// definition attached; a future step decides whether each one is already known. Only scans
    /// lines that aren't themselves ALL-CAPS headings (a heading like "SANCTIONS COMPLIANCE
    /// PROGRAM" would otherwise flood this with every heading word as a false "acronym").
    /// A token must appear 2+ times across the document to be kept — a real acronym gets reused
    /// throughout a document, while a stray capitalized word (formatting emphasis, an OCR
    /// artifact) is almost always a one-off. This alone removes most non-stoplisted noise words
    /// without needing an exhaustive dictionary of ordinary English.</summary>
    public static IReadOnlyList<CandidateAcronym> FindCandidates(string markdownText)
    {
        var all = WalkPages<CandidateAcronym>(markdownText, CandidatesFromText);
        var countByAcronym = all
            .GroupBy(c => c.Acronym, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<CandidateAcronym>();
        foreach (var c in all)
        {
            if (countByAcronym[c.Acronym] < 2) continue;
            if (!seen.Add(c.Acronym)) continue; // one row per acronym, first page it appeared on
            kept.Add(c);
        }

        return kept;
    }

    private static List<T> WalkPages<T>(string markdownText, Action<string, int?, List<T>> perText)
    {
        var results = new List<T>();
        if (string.IsNullOrWhiteSpace(markdownText)) return results;

        var pattern = Regex.Escape(PolicyPageResolver.PageMarkerPrefix) + @"(\d+)\s*-->";
        var pageMatches = Regex.Matches(markdownText, pattern);

        if (pageMatches.Count == 0)
        {
            perText(markdownText, null, results);
            return results;
        }

        for (var i = 0; i < pageMatches.Count; i++)
        {
            var start = pageMatches[i].Index + pageMatches[i].Length;
            var end = i + 1 < pageMatches.Count ? pageMatches[i + 1].Index : markdownText.Length;
            var page = int.Parse(pageMatches[i].Groups[1].Value);
            perText(markdownText[start..end], page, results);
        }

        return results;
    }

    private static void HarvestFromText(string text, int? page, List<HarvestedAcronym> results)
    {
        foreach (Match m in DefinitionThenAcronym().Matches(text))
        {
            // The definition phrase can legitimately be wrapped across a line break in the source
            // PDF — collapse any internal whitespace run (including the newline) to a single space
            // rather than storing a literal "\n" inside the stored definition.
            var def = Regex.Replace(m.Groups["def"].Value.Trim(), @"\s+", " ");
            var acr = m.Groups["acr"].Value.Trim();

            if (def.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < MinDefinitionWords) continue;
            if (def.Length > MaxDefinitionLength) continue;
            if (def == def.ToUpperInvariant()) continue; // "TFS (ABC)" is two acronyms, not a definition
            if (RomanNumeral.IsMatch(acr)) continue;

            results.Add(new HarvestedAcronym(acr, def, page));
        }
    }

    private static void CandidatesFromText(string text, int? page, List<CandidateAcronym> results)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            // A heading/title line is almost entirely uppercase — skip it so every word of
            // "SANCTIONS COMPLIANCE PROGRAM" doesn't get treated as a candidate acronym.
            if (line == line.ToUpperInvariant()) continue;

            foreach (Match m in StandaloneToken().Matches(line))
            {
                var acr = m.Groups["acr"].Value;
                if (RomanNumeral.IsMatch(acr)) continue;
                if (StopWords.Contains(acr)) continue;
                results.Add(new CandidateAcronym(acr, page));
            }
        }
    }
}
