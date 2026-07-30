using System.Text.RegularExpressions;
using Reguliq.Api.Models;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Dedupes and filters Landing AI gov-point extraction artifacts before save/repair.
/// Keep junk-id rules aligned with bcp-web/src/lib/gov-point-filter.ts where possible.
/// </summary>
public static class GovPointExtractNormalizer
{
    private static readonly Regex NumericPointId = new(
        @"^\d+(?:\.\d+)*(?:\([a-z]\))?(?:\s*\([^)]+\))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumericWithParenSuffix = new(
        @"^\d+(?:\.\d+)*\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LegalRefPrefix = new(
        @"^(AML[-/ ]?CFT|Article\s+\d+|Annex\s+\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex[] JunkIdPatterns =
    [
        new(@"^Part\s+[IVXLC]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^Part\s+V\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^Elements?\s+of\s+an?\s+AML", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^The\s+Elements\s+of", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^AML/?CFT\s+Program\s+Element", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^(First|Second|Third)\s+line\s+of\s+defense", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^Scope\s+of\s+Guidelines", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^Part\s+III\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^Part\s+IV\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static readonly HashSet<string> JunkSingleWordIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Complexity", "Controls", "Policies", "Procedures", "Typology", "Size/value",
        "Elements", "Element", "Governance", "Policies", "Procedures",
    };

    public static string NormalizePointNumberKey(string? pointNumber)
    {
        var key = (pointNumber ?? "").Trim().TrimEnd('.');
        return key.ToLowerInvariant();
    }

    public static bool IsValidExtractPointId(string? pointId)
    {
        var id = (pointId ?? "").Trim();
        if (id.Length == 0) return false;
        if (IsJunkExtractPointId(id)) return false;
        if (NumericPointId.IsMatch(id)) return true;
        if (NumericWithParenSuffix.IsMatch(id)) return true;
        if (LegalRefPrefix.IsMatch(id)) return true;
        // Named callout headings (e.g. "FIU Instructions")
        if (char.IsLetter(id[0]) && id.Length >= 4) return true;
        return false;
    }

    public static bool IsJunkExtractPointId(string? pointId)
    {
        var id = (pointId ?? "").Trim();
        if (id.Length == 0) return true;
        if (JunkSingleWordIds.Contains(id)) return true;
        foreach (var pattern in JunkIdPatterns)
        {
            if (pattern.IsMatch(id)) return true;
        }

        if (char.IsDigit(id[0])) return false;
        if (LegalRefPrefix.IsMatch(id)) return false;

        if (id.Contains(" - ", StringComparison.Ordinal) && !LegalRefPrefix.IsMatch(id))
            return true;

        if (Regex.IsMatch(id, @"^Element[s]?\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(id, @"^\d"))
            return true;

        if (Regex.IsMatch(id, @"^Part\s", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    public static int ScorePoint(GovPoint point)
    {
        var score = (point.Text ?? "").Trim().Length;
        if (!string.IsNullOrWhiteSpace(point.Title)) score += 40;
        if (point.PageHint is > 1) score += 600;
        else if (point.PageHint is > 0) score += 80;
        return score;
    }

    public static List<GovPoint> DedupeAndFilter(IEnumerable<GovPoint> points)
    {
        var valid = points
            .Where(p => !string.IsNullOrWhiteSpace(p.PointId) && !string.IsNullOrWhiteSpace(p.Text))
            .Where(p => IsValidExtractPointId(p.PointId))
            .Select(NormalizeWhitespace)
            .ToList();

        var withParents = SynthesizeMissingParentPoints(valid);
        return AssignNestedIdsToDuplicateSiblings(withParents);
    }

    /// <summary>
    /// When children exist (3.1.1) but the parent section row (3.1) is missing, synthesize a
    /// section-heading point from child section/title metadata so UI and gap analysis stay complete.
    /// </summary>
    public static List<GovPoint> SynthesizeMissingParentPoints(IReadOnlyList<GovPoint> points)
    {
        if (points.Count == 0) return [];

        var byKey = new Dictionary<string, GovPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in points)
        {
            var key = NormalizePointNumberKey(point.PointId);
            if (!string.IsNullOrWhiteSpace(key) && !byKey.ContainsKey(key))
                byKey[key] = point;
        }

        var parentsNeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in byKey.Keys.ToList())
        {
            var parts = key.Split('.');
            for (var depth = 1; depth < parts.Length; depth++)
            {
                var parent = string.Join('.', parts.Take(depth));
                if (!byKey.ContainsKey(parent))
                    parentsNeeded.Add(parent);
            }
        }

        if (parentsNeeded.Count == 0) return points.ToList();

        var output = points.ToList();
        foreach (var parentId in parentsNeeded.OrderBy(x => x, StringComparer.Ordinal))
        {
            var children = points
                .Where(p =>
                {
                    var id = NormalizePointNumberKey(p.PointId);
                    return !string.IsNullOrWhiteSpace(id)
                           && id.StartsWith($"{parentId}.", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            var sectionLine = ResolveParentSectionLine(parentId, children);
            var title = sectionLine is not null
                ? HeadingTitleFromSectionLine(sectionLine)
                : null;

            output.Add(new GovPoint(
                parentId,
                title,
                title ?? $"Section {parentId}",
                sectionLine,
                children.Select(c => c.PageHint).FirstOrDefault(h => h is > 0),
                "informational"));
        }

        return output;
    }

    private static string? ResolveParentSectionLine(string parentId, IReadOnlyList<GovPoint> children)
    {
        foreach (var child in children)
        {
            var fromSection = HeadingTitleFromSectionLine(child.Section);
            if (fromSection is not null)
                return $"{parentId}. {fromSection}";

            var embedded = HeadingTitleEmbeddedForClause(parentId, child.Section)
                           ?? HeadingTitleEmbeddedForClause(parentId, child.Title)
                           ?? HeadingTitleEmbeddedForClause(parentId, child.Text);
            if (embedded is not null)
                return $"{parentId}. {embedded}";
        }

        return null;
    }

    private static string? HeadingTitleFromSectionLine(string? section)
    {
        var s = (section ?? "").Trim();
        if (s.Length == 0) return null;

        var withoutPage = Regex.Replace(s, @"\s*·\s*p\.\s*\d+.*$", "", RegexOptions.IgnoreCase).Trim();
        var segments = withoutPage.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var seg = segments[i];
            var m = Regex.Match(seg, @"^\d+(?:\.\d+)*\.?\s+(.+)$");
            if (m.Success && m.Groups[1].Value.Trim().Length > 0)
                return m.Groups[1].Value.Trim();
        }

        var whole = Regex.Match(withoutPage, @"^\d+(?:\.\d+)*\.?\s+(.+)$");
        if (whole.Success && whole.Groups[1].Value.Trim().Length > 0)
            return whole.Groups[1].Value.Trim();

        return null;
    }

    private static string? HeadingTitleEmbeddedForClause(string norm, string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return null;

        var escaped = Regex.Escape(norm).Replace("\\.", "\\.");
        var patterns = new[]
        {
            $@"\b{escaped}\.\s+([^·\n]{{4,}}?)(?:\s*·|$)",
            $@"\b{escaped}\s+([A-Z][^·\n]{{4,}}?)(?:\s*·|$)",
        };

        foreach (var pattern in patterns)
        {
            var m = Regex.Match(s, pattern, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Trim().Length > 0)
                return m.Groups[1].Value.Trim();
        }

        return null;
    }

    /// <summary>
    /// When Landing AI emits multiple distinct rows with the same number (e.g. four "7.8" sub-topics),
    /// keep them all but nest as 7.8.1, 7.8.2, … True duplicate content is collapsed to one row.
    /// </summary>
    public static List<GovPoint> AssignNestedIdsToDuplicateSiblings(IReadOnlyList<GovPoint> points)
    {
        if (points.Count == 0) return [];

        var groups = points
            .GroupBy(p => NormalizePointNumberKey(p.PointId), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var singletonKeys = new HashSet<string>(
            groups.Where(kv => kv.Value.Count == 1).Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);

        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in points)
        {
            var key = NormalizePointNumberKey(point.PointId);
            if (singletonKeys.Contains(key))
                occupied.Add(key);
        }

        var output = new List<GovPoint>();
        foreach (var point in points)
        {
            var key = NormalizePointNumberKey(point.PointId);
            if (string.IsNullOrWhiteSpace(key) || !groups.TryGetValue(key, out var group))
            {
                output.Add(point);
                continue;
            }

            if (group.Count <= 1)
            {
                output.Add(point);
                groups.Remove(key);
                continue;
            }

            var distinct = CollapseNearDuplicatePoints(group);
            if (distinct.Count <= 1)
            {
                output.Add(distinct[0]);
                groups.Remove(key);
                continue;
            }

            var suffix = 1;
            foreach (var sibling in distinct)
            {
                var nestedId = $"{key}.{suffix}";
                while (occupied.Contains(nestedId))
                {
                    suffix++;
                    nestedId = $"{key}.{suffix}";
                }

                output.Add(string.Equals(sibling.PointId, nestedId, StringComparison.OrdinalIgnoreCase)
                    ? sibling
                    : sibling with { PointId = nestedId });
                occupied.Add(nestedId);
                suffix++;
            }

            groups.Remove(key);
        }

        return output
            .OrderBy(p => p.PointId, StringComparer.Ordinal)
            .ToList();
    }

    public static GovPoint NormalizeWhitespace(GovPoint point) =>
        point with
        {
            PointId = point.PointId.Trim(),
            Title = string.IsNullOrWhiteSpace(point.Title) ? null : point.Title.Trim(),
            Text = point.Text.Trim(),
            Section = string.IsNullOrWhiteSpace(point.Section) ? null : point.Section.Trim(),
        };

    public sealed record RepairPlan(
        IReadOnlyList<Guid> SoftDeleteIds,
        IReadOnlyList<Guid> KeepIds,
        IReadOnlyDictionary<Guid, string> RenumberTo,
        int DuplicateGroups,
        int JunkRemoved);

    public static RepairPlan PlanRepair<TPoint>(
        IEnumerable<TPoint> points,
        Func<TPoint, Guid> idSelector,
        Func<TPoint, string> numberSelector,
        Func<TPoint, string?> titleSelector,
        Func<TPoint, string> contentSelector,
        Func<TPoint, int?> pageSelector)
    {
        var active = points.ToList();
        var softDelete = new HashSet<Guid>();
        var keep = new HashSet<Guid>();
        var renumberTo = new Dictionary<Guid, string>();
        var duplicateGroups = 0;

        foreach (var point in active)
        {
            if (IsJunkExtractPointId(numberSelector(point)))
                softDelete.Add(idSelector(point));
        }

        var survivors = active.Where(p => !softDelete.Contains(idSelector(p))).ToList();
        var singletonKeys = survivors
            .GroupBy(p => NormalizePointNumberKey(numberSelector(p)), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() == 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var occupied = survivors
            .Where(p => singletonKeys.Contains(NormalizePointNumberKey(numberSelector(p))))
            .Select(p => NormalizePointNumberKey(numberSelector(p)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var groups = survivors
            .GroupBy(p => NormalizePointNumberKey(numberSelector(p)), StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Key)) continue;
            var ordered = group
                .OrderByDescending(p => ScoreStoredPoint(
                    contentSelector(p),
                    titleSelector(p),
                    pageSelector(p)))
                .ToList();
            if (ordered.Count <= 1)
            {
                keep.Add(idSelector(ordered[0]));
                continue;
            }

            duplicateGroups++;
            var distinct = CollapseNearDuplicateStoredRows(
                ordered,
                titleSelector,
                contentSelector);
            if (distinct.Count <= 1)
            {
                keep.Add(idSelector(distinct[0]));
                foreach (var dup in ordered.Where(p => idSelector(p) != idSelector(distinct[0])))
                    softDelete.Add(idSelector(dup));
                continue;
            }

            var suffix = 1;
            foreach (var row in distinct)
            {
                var nestedId = $"{group.Key}.{suffix}";
                while (occupied.Contains(nestedId))
                {
                    suffix++;
                    nestedId = $"{group.Key}.{suffix}";
                }

                var rowId = idSelector(row);
                keep.Add(rowId);
                if (!string.Equals(numberSelector(row), nestedId, StringComparison.OrdinalIgnoreCase))
                    renumberTo[rowId] = nestedId;
                occupied.Add(nestedId);
                suffix++;
            }

            foreach (var dup in ordered.Where(p => !keep.Contains(idSelector(p))))
                softDelete.Add(idSelector(dup));
        }

        return new RepairPlan(
            softDelete.ToList(),
            keep.ToList(),
            renumberTo,
            duplicateGroups,
            active.Count(p => IsJunkExtractPointId(numberSelector(p))));
    }

    private static List<GovPoint> CollapseNearDuplicatePoints(IReadOnlyList<GovPoint> points)
    {
        var distinct = new List<GovPoint>();
        foreach (var point in points.OrderByDescending(ScorePoint))
        {
            if (distinct.Any(existing => IsNearDuplicatePoint(existing, point)))
                continue;
            distinct.Add(point);
        }

        return distinct.OrderBy(p => p.PointId, StringComparer.Ordinal).ToList();
    }

    private static List<TPoint> CollapseNearDuplicateStoredRows<TPoint>(
        IReadOnlyList<TPoint> points,
        Func<TPoint, string?> titleSelector,
        Func<TPoint, string> contentSelector)
    {
        var distinct = new List<TPoint>();
        foreach (var point in points)
        {
            if (distinct.Any(existing =>
                    IsNearDuplicateContent(contentSelector(existing), contentSelector(point))
                    && string.Equals(
                        (titleSelector(existing) ?? "").Trim(),
                        (titleSelector(point) ?? "").Trim(),
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            distinct.Add(point);
        }

        return distinct;
    }

    private static bool IsNearDuplicatePoint(GovPoint a, GovPoint b) =>
        IsNearDuplicateContent(a.Text, b.Text)
        && string.Equals((a.Title ?? "").Trim(), (b.Title ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsNearDuplicateContent(string? a, string? b)
    {
        var ta = NormalizeContentFingerprint(a);
        var tb = NormalizeContentFingerprint(b);
        if (ta.Length == 0 || tb.Length == 0) return false;
        if (ta == tb) return true;

        var shorter = ta.Length <= tb.Length ? ta : tb;
        var longer = ta.Length <= tb.Length ? tb : ta;
        return longer.Contains(shorter, StringComparison.Ordinal);
    }

    private static string NormalizeContentFingerprint(string? text) =>
        Regex.Replace((text ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

    private static int ScoreStoredPoint(string content, string? title, int? page)
    {
        var score = content.Trim().Length;
        if (!string.IsNullOrWhiteSpace(title)) score += 40;
        if (page is > 1) score += 600;
        else if (page is > 0) score += 80;
        return score;
    }
}
