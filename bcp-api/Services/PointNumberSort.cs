using System.Text.RegularExpressions;

namespace Reguliq.Api.Services;

/// <summary>Numeric-aware sort for regulation point numbers (2.1.2 before 2.1.10).</summary>
public static partial class PointNumberSort
{
    [GeneratedRegex(@"^(\d+(?:\.\d+)*)")]
    private static partial Regex NumericPrefix();

    public static int Compare(string? a, string? b)
    {
        var left = (a ?? "").Trim();
        var right = (b ?? "").Trim();
        if (left.Length == 0 && right.Length == 0) return 0;
        if (left.Length == 0) return 1;
        if (right.Length == 0) return -1;

        var leftParts = ParseParts(left);
        var rightParts = ParseParts(right);
        var max = Math.Max(leftParts.Count, rightParts.Count);
        for (var i = 0; i < max; i++)
        {
            var lv = i < leftParts.Count ? leftParts[i] : -1;
            var rv = i < rightParts.Count ? rightParts[i] : -1;
            if (lv >= 0 && rv >= 0)
            {
                var cmp = lv.CompareTo(rv);
                if (cmp != 0) return cmp;
                continue;
            }

            if (lv >= 0) return -1;
            if (rv >= 0) return 1;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<T> OrderByPointNumber<T>(IEnumerable<T> items, Func<T, string?> selector) =>
        items.OrderBy(selector, Comparer<string?>.Create((a, b) => Compare(a, b)));

    private static List<int> ParseParts(string value)
    {
        var match = NumericPrefix().Match(value);
        if (!match.Success) return [];

        return match.Groups[1].Value
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(static p => int.TryParse(p, out var n) ? n : -1)
            .ToList();
    }
}
