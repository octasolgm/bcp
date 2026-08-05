using System.Text;
using System.Text.RegularExpressions;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.Pdf;

/// <summary>
/// Align Landing AI parse markdown with real PDF viewer pages (Regul.ai-style dense markers).
/// Landing text quality + PdfPig page boundaries — used only for page ref resolution, not extract.
/// </summary>
public static partial class PdfGroundedMarkdownBuilder
{
    private const int MinAnchorChars = 32;
    private const int MaxAnchorChars = 220;

    /// <summary>
    /// Inject one <see cref="PolicyPageResolver.PageMarkerPrefix"/> marker per PDF viewer page into Landing markdown.
    /// </summary>
    public static string? TryGround(string? landingMarkdown, byte[] pdfBytes)
    {
        if (string.IsNullOrWhiteSpace(landingMarkdown) || pdfBytes.Length < 16)
            return null;

        var native = PdfNativePageDocument.TryCreate(pdfBytes);
        return native is null ? null : TryGround(landingMarkdown, native);
    }

    /// <summary>Ground Landing markdown using an existing native per-page document.</summary>
    public static string? TryGround(string? landingMarkdown, PdfNativePageDocument native)
    {
        if (string.IsNullOrWhiteSpace(landingMarkdown) || native.TotalPages <= 0)
            return null;

        var stripped = StripExistingMarkers(landingMarkdown);
        if (stripped.Length < 80)
            return native.Markdown;

        var anchors = BuildPageAnchors(native, stripped);
        if (anchors.Count == 0)
            return native.Markdown;

        return InjectMarkers(stripped, anchors);
    }

    /// <summary>Prefer grounded Landing markdown; fall back to PdfPig-only markdown.</summary>
    public static string? TryBuildResolveMarkdown(string? landingMarkdown, byte[] pdfBytes)
    {
        var grounded = TryGround(landingMarkdown, pdfBytes);
        if (!string.IsNullOrWhiteSpace(grounded)
            && PolicyPageResolver.EstimatePageCount(grounded) is int pages
            && pages > 1)
        {
            return grounded;
        }

        return PdfNativePageDocument.TryCreate(pdfBytes)?.Markdown;
    }

    private static List<(int Index, int Page)> BuildPageAnchors(PdfNativePageDocument native, string landingMarkdown)
    {
        var segments = PolicyPageResolver.SplitMarkdownIntoPageSegments(native.Markdown);
        var anchors = new List<(int Index, int Page)>();
        var searchFrom = 0;

        foreach (var (page, text) in segments.OrderBy(s => s.Page))
        {
            var anchor = ExtractAnchor(text);
            if (anchor.Length < MinAnchorChars)
                continue;

            var idx = FindAnchorIndex(landingMarkdown, anchor, searchFrom);
            if (idx < 0)
                continue;

            if (anchors.Count == 0 || idx > anchors[^1].Index)
            {
                anchors.Add((idx, page));
                searchFrom = idx + Math.Min(anchor.Length, 40);
            }
        }

        return anchors;
    }

    private static string ExtractAnchor(string pageText)
    {
        var lines = pageText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length >= 12)
            .Where(l => !PageFooterRegex().IsMatch(l))
            .Where(l => !l.StartsWith("Page |", StringComparison.OrdinalIgnoreCase))
            .Take(4);

        var joined = string.Join(' ', lines);
        if (joined.Length > MaxAnchorChars)
            joined = joined[..MaxAnchorChars];
        return Normalize(joined);
    }

    private static int FindAnchorIndex(string haystack, string anchor, int startAt)
    {
        if (anchor.Length < MinAnchorChars) return -1;

        var normalizedHay = Normalize(haystack);
        var idx = normalizedHay.IndexOf(anchor, startAt, StringComparison.Ordinal);
        if (idx >= 0)
            return MapNormalizedIndexToRaw(haystack, idx);

        if (anchor.Length >= 48)
        {
            var shortAnchor = anchor[..48];
            idx = normalizedHay.IndexOf(shortAnchor, startAt, StringComparison.Ordinal);
            if (idx >= 0)
                return MapNormalizedIndexToRaw(haystack, idx);
        }

        return -1;
    }

    private static int MapNormalizedIndexToRaw(string raw, int normalizedIndex)
    {
        var normPos = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (normPos == normalizedIndex)
                return i;
            if (!char.IsWhiteSpace(raw[i]) || (i > 0 && !char.IsWhiteSpace(raw[i - 1])))
                normPos++;
        }

        return Math.Clamp(normalizedIndex, 0, Math.Max(0, raw.Length - 1));
    }

    private static string InjectMarkers(string markdown, List<(int Index, int Page)> anchors)
    {
        var sb = new StringBuilder(markdown.Length + anchors.Count * 40);
        var cursor = 0;
        foreach (var (index, page) in anchors.OrderBy(a => a.Index))
        {
            if (index < cursor) continue;
            sb.Append(markdown.AsSpan(cursor, index - cursor));
            sb.Append(PolicyPageResolver.PageMarkerPrefix);
            sb.Append(page);
            sb.AppendLine(" -->");
            cursor = index;
        }

        if (cursor < markdown.Length)
            sb.Append(markdown.AsSpan(cursor));

        return sb.ToString().Trim();
    }

    private static string StripExistingMarkers(string markdown)
    {
        var without = MarkerRegex().Replace(markdown, "\n");
        return Regex.Replace(without, "\n{3,}", "\n\n").Trim();
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var chars = value.Where(c => !char.IsControl(c) || c == ' ').ToArray();
        var s = new string(chars).ToLowerInvariant();
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    [GeneratedRegex(@"<!--\s*BCP_PDF_PAGE:\d+\s*-->", RegexOptions.IgnoreCase)]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"^\s*P\s*a\s*g\s*e\s*\|", RegexOptions.IgnoreCase)]
    private static partial Regex PageFooterRegex();
}
