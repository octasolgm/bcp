using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Unique analysis-run counts per internal or regulation document (ND + legacy runs).
/// </summary>
public sealed class NdDocumentAnalysisRunCounts(
    Dictionary<Guid, HashSet<Guid>> runIdsByDocId,
    Dictionary<string, HashSet<Guid>> runIdsByInternalHash,
    Dictionary<string, HashSet<Guid>> runIdsByGovHash)
{
    public int CountForInternal(Guid documentId, string? fileHash) =>
        UniqueCount([documentId], fileHash, govHash: null);

    public int CountForRegulation(Guid documentId, Guid? storedDocumentId, string? fileHash) =>
        UniqueCount([documentId, storedDocumentId], internalHash: null, fileHash);

    private int UniqueCount(IEnumerable<Guid?> ids, string? internalHash, string? govHash)
    {
        var runs = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (id is not Guid guid || guid == Guid.Empty) continue;
            if (runIdsByDocId.TryGetValue(guid, out var set))
                runs.UnionWith(set);
        }

        if (!string.IsNullOrWhiteSpace(internalHash)
            && runIdsByInternalHash.TryGetValue(internalHash, out var byInternal))
            runs.UnionWith(byInternal);

        if (!string.IsNullOrWhiteSpace(govHash)
            && runIdsByGovHash.TryGetValue(govHash, out var byGov))
            runs.UnionWith(byGov);

        return runs.Count;
    }
}

public static class NdDocumentAnalysisRunCountHelper
{
    public static readonly NdDocumentAnalysisRunCounts Empty = new([], [], []);

    /// <summary>
    /// Builds the run-count map for the document lists.
    ///
    /// <paramref name="demoCtx"/> scopes the runs the same way the document lists are
    /// scoped: a demo viewer counts only demo runs, and a real user never counts them.
    /// Without it a demo document reads as used in every real user's analysis too.
    /// </summary>
    public static async Task<NdDocumentAnalysisRunCounts> LoadAsync(
        AppDbContext db,
        CancellationToken ct,
        NdDemoIsolationContext? demoCtx = null)
    {
        var byDoc = new Dictionary<Guid, HashSet<Guid>>();
        var byInternalHash = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var byGovHash = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var query = db.NdAnalysisRuns.AsNoTracking().Where(r => r.Status != "deleted");
            if (demoCtx != null) query = NdDemoDataFilters.ApplyToAnalysisRuns(query, demoCtx);

            var ndRuns = await query
                .Select(r => new { r.Id, r.SelectedInternalDocIds, r.SelectedRegulationDocIds })
                .ToListAsync(ct);

            foreach (var run in ndRuns)
            {
                foreach (var id in ParseIds(run.SelectedInternalDocIds))
                    Add(byDoc, id, run.Id);
                foreach (var id in ParseIds(run.SelectedRegulationDocIds))
                    Add(byDoc, id, run.Id);
            }
        }
        catch
        {
            /* table may not exist */
        }

        try
        {
            // Legacy runs predate demo isolation, so a demo viewer never counts them.
            if (demoCtx is { Enabled: true, ViewerIsDemo: true })
                return new NdDocumentAnalysisRunCounts(byDoc, byInternalHash, byGovHash);

            var legacy = await db.DocumentAnalysisRuns.AsNoTracking()
                .Select(r => new
                {
                    r.Id,
                    r.InternalDocumentId,
                    r.RegulationDocumentId,
                    r.InternalFileHash,
                    r.GovFileHash,
                })
                .ToListAsync(ct);

            foreach (var run in legacy)
            {
                Add(byDoc, run.InternalDocumentId, run.Id);
                Add(byDoc, run.RegulationDocumentId, run.Id);
                AddHash(byInternalHash, run.InternalFileHash, run.Id);
                AddHash(byGovHash, run.GovFileHash, run.Id);
            }
        }
        catch
        {
            /* table may not exist */
        }

        return new NdDocumentAnalysisRunCounts(byDoc, byInternalHash, byGovHash);
    }

    private static void Add(Dictionary<Guid, HashSet<Guid>> map, Guid? documentId, Guid runId)
    {
        if (documentId is not Guid id || id == Guid.Empty) return;
        if (!map.TryGetValue(id, out var set))
        {
            set = [];
            map[id] = set;
        }
        set.Add(runId);
    }

    private static void AddHash(Dictionary<string, HashSet<Guid>> map, string? hash, Guid runId)
    {
        if (string.IsNullOrWhiteSpace(hash)) return;
        if (!map.TryGetValue(hash, out var set))
        {
            set = [];
            map[hash] = set;
        }
        set.Add(runId);
    }

    private static List<Guid> ParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            var list = new List<Guid>(ids.Count);
            foreach (var s in ids)
            {
                if (Guid.TryParse(s, out var g)) list.Add(g);
            }

            return list;
        }
        catch
        {
            return [];
        }
    }
}
