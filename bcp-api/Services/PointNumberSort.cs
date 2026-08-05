using System.Text.RegularExpressions;

namespace Reguliq.Api.Services;

/// <summary>Numeric-aware sort for point/section refs (2.1.2 before 2.1.10; 6.18-a before 6.18-b).</summary>
public static partial class PointNumberSort
{
    [GeneratedRegex(@"^(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex NumericPrefix();

    [GeneratedRegex(@"^(\d+)([a-z]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SegmentToken();

    public static int Compare(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 && right.Length == 0) return 0;
        if (left.Length == 0) return 1;
        if (right.Length == 0) return -1;

        var leftTokens = ParseTokens(left);
        var rightTokens = ParseTokens(right);
        var max = Math.Max(leftTokens.Count, rightTokens.Count);
        for (var i = 0; i < max; i++)
        {
            var lt = i < leftTokens.Count ? leftTokens[i] : default;
            var rt = i < rightTokens.Count ? rightTokens[i] : default;

            if (lt.Num >= 0 && rt.Num >= 0)
            {
                var cmp = lt.Num.CompareTo(rt.Num);
                if (cmp != 0) return cmp;
                cmp = string.Compare(lt.Suffix, rt.Suffix, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                continue;
            }

            if (lt.Num >= 0) return -1;
            if (rt.Num >= 0) return 1;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<T> OrderByPointNumber<T>(IEnumerable<T> items, Func<T, string?> selector) =>
        items.OrderBy(selector, Comparer<string?>.Create((a, b) => Compare(a, b)));

    private static string Normalize(string? value) =>
        (value ?? "").Trim().TrimStart('§').Trim();

    private static List<(int Num, string Suffix)> ParseTokens(string value)
    {
        var head = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;
        head = head.TrimEnd('.');

        if (head.Contains('.', StringComparison.Ordinal))
        {
            return head
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseSegment)
                .ToList();
        }

        var prefix = NumericPrefix().Match(head);
        if (!prefix.Success)
            return [ParseSegment(head)];

        var numericPart = prefix.Groups[1].Value;
        var suffixPart = head[prefix.Length..];
        var tokens = numericPart
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => (int.TryParse(p, out var n) ? n : -1, ""))
            .ToList();

        if (suffixPart.Length > 0)
        {
            var seg = ParseSegment(suffixPart.TrimStart('-', '.'));
            if (seg.Num >= 0 || seg.Suffix.Length > 0)
                tokens.Add(seg);
        }

        return tokens;
    }

    private static (int Num, string Suffix) ParseSegment(string segment)
    {
        var m = SegmentToken().Match(segment);
        if (m.Success)
            return (int.Parse(m.Groups[1].Value), m.Groups[2].Value.ToLowerInvariant());

        return int.TryParse(segment, out var n) ? (n, "") : (-1, segment.ToLowerInvariant());
    }
}
