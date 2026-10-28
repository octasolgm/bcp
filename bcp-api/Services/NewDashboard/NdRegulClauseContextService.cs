using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Builds the "supporting regulatory context" for one clause: where it sits in its regulation, as headings
/// only. For clause 3.5 that is the parent heading (3), every sibling at the same level (3.1 - 3.11, with 3.5
/// marked) and every sub-clause heading under 3.5. It tells the judge what the surrounding clauses cover so it
/// does not pull a sibling's subject into this clause; the clause text itself is still the only thing judged.
/// Headings come from the regulation's extracted points, so nothing extra is sent to or asked of the AI.
/// </summary>
public class NdRegulClauseContextService(AppDbContext db)
{
    private const int MaxHeadingChars = 160;

    private static readonly Regex DottedNumber = new(@"^\d+(\.\d+)*$", RegexOptions.Compiled);

    public sealed record Heading(string Number, string Title);

    public sealed record ClauseContext(
        string RegulationName,
        IReadOnlyList<Heading> Ancestors,
        IReadOnlyList<Heading> Siblings,
        IReadOnlyList<Heading> Children,
        string ClauseNumber,
        string Text);

    private sealed record RegulationOutline(string Name, IReadOnlyDictionary<string, Heading> ByNumber);

    // One regulation's outline is loaded once per service instance (one run / one rerun request).
    private readonly Dictionary<Guid, RegulationOutline?> _outlines = [];

    /// <summary>Null when the clause has no source regulation point or no dotted number (e.g. a manual
    /// clause), in which case the prompt says no supporting context is available.</summary>
    public async Task<ClauseContext?> BuildAsync(NdAnalysisPoint point, string clauseNo, CancellationToken ct)
    {
        var number = NormalizeNumber(clauseNo);
        if (number == null) return null;

        Guid? docId = null;
        if (point.RegulationPointId is Guid regPointId)
            docId = await db.NdRegulationPoints.AsNoTracking()
                .Where(p => p.Id == regPointId)
                .Select(p => (Guid?)p.RegulationDocumentId)
                .FirstOrDefaultAsync(ct);
        // Points picked on the V5 new analysis page carry no regulation point link, only a "{docId}:{number}" id.
        docId ??= RegulationDocumentIdFromSnapshot(point.PointSnapshot);
        if (docId is not Guid regulationDocumentId) return null;

        var outline = await LoadOutlineAsync(regulationDocumentId, ct);
        return outline == null ? null : Build(outline.Name, outline.ByNumber, number);
    }

    private async Task<RegulationOutline?> LoadOutlineAsync(Guid regulationDocumentId, CancellationToken ct)
    {
        if (_outlines.TryGetValue(regulationDocumentId, out var cached)) return cached;

        var name = await db.NdRegulationDocuments.AsNoTracking()
            .Where(d => d.Id == regulationDocumentId)
            .Select(d => d.Name)
            .FirstOrDefaultAsync(ct);
        var rows = await db.NdRegulationPoints.AsNoTracking()
            .Where(p => p.RegulationDocumentId == regulationDocumentId && p.Status == NdRegulationPointStatus.Active)
            .Select(p => new { p.PointNumber, p.PointTitle, p.PointContent })
            .ToListAsync(ct);

        var byNumber = new Dictionary<string, Heading>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var number = NormalizeNumber(row.PointNumber);
            if (number == null || byNumber.ContainsKey(number)) continue;
            byNumber[number] = new Heading(number, HeadingTitle(number, row.PointTitle, row.PointContent));
        }

        var outline = byNumber.Count == 0 ? null : new RegulationOutline(name ?? "Regulation", byNumber);
        _outlines[regulationDocumentId] = outline;
        return outline;
    }

    /// <summary>Pure hierarchy + formatting step (unit-testable without a database).</summary>
    public static ClauseContext Build(string regulationName, IReadOnlyDictionary<string, Heading> byNumber, string clauseNumber)
    {
        var segments = clauseNumber.Split('.');
        var parentPrefix = segments.Length > 1 ? string.Join('.', segments[..^1]) : null;

        var ancestors = new List<Heading>();
        for (var k = 1; k < segments.Length; k++)
        {
            var n = string.Join('.', segments[..k]);
            ancestors.Add(byNumber.TryGetValue(n, out var h) ? h : new Heading(n, ""));
        }

        var siblings = byNumber.Values
            .Where(h => SegmentCount(h.Number) == segments.Length
                && (parentPrefix == null || h.Number.StartsWith(parentPrefix + ".", StringComparison.Ordinal)))
            .OrderBy(h => h.Number, NumberComparer.Instance)
            .ToList();
        if (!siblings.Any(h => h.Number == clauseNumber))
        {
            siblings.Add(byNumber.TryGetValue(clauseNumber, out var self) ? self : new Heading(clauseNumber, ""));
            siblings.Sort((a, b) => NumberComparer.Instance.Compare(a.Number, b.Number));
        }

        var children = byNumber.Values
            .Where(h => h.Number.StartsWith(clauseNumber + ".", StringComparison.Ordinal))
            .OrderBy(h => h.Number, NumberComparer.Instance)
            .ToList();

        return new ClauseContext(
            regulationName, ancestors, siblings, children, clauseNumber,
            Format(regulationName, ancestors, siblings, children, clauseNumber, parentPrefix));
    }

    private static string Format(
        string regulationName,
        IReadOnlyList<Heading> ancestors,
        IReadOnlyList<Heading> siblings,
        IReadOnlyList<Heading> children,
        string clauseNumber,
        string? parentPrefix)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Regulation: {regulationName}");

        sb.AppendLine("Parent headings:");
        if (ancestors.Count == 0) sb.AppendLine("  (none - this is a top-level clause)");
        foreach (var a in ancestors) sb.AppendLine($"  {Line(a)}");

        sb.AppendLine(parentPrefix == null
            ? "Clauses at the same level (top-level chapters):"
            : $"Clauses at the same level under {parentPrefix}:");
        // Every sibling and sub-clause heading is listed (no line limit): headings are short, and a cut list
        // would hide exactly the neighbouring subjects this block exists to show.
        foreach (var s in siblings)
            sb.AppendLine(s.Number == clauseNumber ? $"  {Line(s)}   <-- THIS CLAUSE (the one being judged)" : $"  {Line(s)}");

        sb.AppendLine($"Sub-clauses of {clauseNumber}:");
        if (children.Count == 0) sb.AppendLine("  (none)");
        foreach (var c in children) sb.AppendLine($"  {Line(c)}");

        return sb.ToString().TrimEnd();
    }

    private static string Line(Heading h) => string.IsNullOrWhiteSpace(h.Title) ? h.Number : $"{h.Number} {h.Title}";

    private static int SegmentCount(string number) => number.Count(c => c == '.') + 1;

    /// <summary>The regulation document id from a point snapshot whose pointId is "{documentId}:{pointNumber}".</summary>
    public static Guid? RegulationDocumentIdFromSnapshot(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(snapshotJson);
            if (!doc.RootElement.TryGetProperty("pointId", out var pid) || pid.ValueKind != System.Text.Json.JsonValueKind.String)
                return null;
            var raw = pid.GetString() ?? "";
            var colon = raw.IndexOf(':');
            return colon > 0 && Guid.TryParse(raw[..colon], out var id) ? id : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>"3." / " 3.5 " -> "3" / "3.5"; null for anything that is not a dotted number.</summary>
    public static string? NormalizeNumber(string? raw)
    {
        var n = (raw ?? "").Trim().TrimEnd('.').Trim();
        return DottedNumber.IsMatch(n) ? n : null;
    }

    public static string HeadingTitle(string number, string? title, string? content)
    {
        var text = (title ?? "").Trim();
        if (text.Length == 0)
        {
            text = (content ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            // Content usually repeats the number ("3.5 Money Laundering ..."); drop it.
            if (text.StartsWith(number, StringComparison.Ordinal))
                text = text[number.Length..].TrimStart('.', ' ', '\t');
        }
        text = Regex.Replace(text, @"\s+", " ");
        return text.Length > MaxHeadingChars ? text[..MaxHeadingChars].TrimEnd() + "..." : text;
    }

    private sealed class NumberComparer : IComparer<string>
    {
        public static readonly NumberComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = (x ?? "").Split('.');
            var b = (y ?? "").Split('.');
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                long.TryParse(a[i], out var p);
                long.TryParse(b[i], out var q);
                var c = p.CompareTo(q);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }
    }
}
