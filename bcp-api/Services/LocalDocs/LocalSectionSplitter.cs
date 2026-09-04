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
    // Numbered heading: "9.4.1 Independent audit", "6.2. Title", up to 4 levels deep.
    [GeneratedRegex(@"^(?<no>\d{1,3}(\.\d{1,3}){0,4})\.?\s+(?<title>[A-Za-z].{2,})$")]
    private static partial Regex NumberedHeading();

    // Labelled heading: "Article 12", "Section 6.2", "Rule 9.4.1", "Clause 3".
    [GeneratedRegex(@"^(?<label>Article|Section|Rule|Clause|Chapter|Annex)\s+(?<no>\d{1,3}(\.\d{1,3}){0,4})\b\.?\s*(?<title>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LabelledHeading();

    private const int MinHeadingLineLength = 3;
    private const int MaxHeadingLineLength = 160;

    public static IReadOnlyList<LocalSection> Split(IReadOnlyList<LocalPageResult> pages)
    {
        var sections = new List<LocalSection>();
        string? currentNo = null;
        var currentText = new System.Text.StringBuilder();
        int? currentPage = null;

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

            foreach (var rawLine in page.Text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;

                var headingNo = TryMatchHeading(line);
                if (headingNo != null)
                {
                    Flush();
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
        return sections;
    }

    private static string? TryMatchHeading(string line)
    {
        if (line.Length is < MinHeadingLineLength or > MaxHeadingLineLength) return null;

        var numbered = NumberedHeading().Match(line);
        if (numbered.Success) return numbered.Groups["no"].Value;

        var labelled = LabelledHeading().Match(line);
        if (labelled.Success) return $"{labelled.Groups["label"].Value} {labelled.Groups["no"].Value}";

        return null;
    }
}
