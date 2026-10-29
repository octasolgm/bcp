using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// A workspace's upload limits (set by the platform admin on Business workspaces, shown on Business
/// settings): the largest single document, the largest combined size of one multi-file upload, and the
/// total size of its document library. All in MB; null means no workspace limit.
///
/// The server enforces the per-document and total storage limits on every upload. The per-upload limit
/// covers a batch of files chosen together, which the server receives one file at a time, so the upload
/// pages check it before they start.
/// </summary>
public static class NdUploadLimits
{
    /// <summary>The upload endpoints' own request cap (50 MB); a workspace limit cannot go above it.</summary>
    public const int ServerMaxDocumentMb = 50;

    private const long BytesPerMb = 1024L * 1024L;

    public sealed record Limits(int? MaxDocumentMb, int? MaxUploadMb, int? MaxStorageMb, long UsedStorageBytes);

    public static async Task<Limits> GetAsync(AppDbContext db, Guid workspaceId, CancellationToken ct)
    {
        var ws = await db.NdWorkspaces.AsNoTracking()
            .Where(w => w.Id == workspaceId)
            .Select(w => new { w.MaxDocumentMb, w.MaxUploadMb, w.MaxStorageMb })
            .FirstOrDefaultAsync(ct);
        var used = await UsedStorageBytesAsync(db, workspaceId, ct);
        return new Limits(ws?.MaxDocumentMb, ws?.MaxUploadMb, ws?.MaxStorageMb, used);
    }

    /// <summary>Why this file cannot be uploaded to the current workspace, or null when it fits.</summary>
    public static async Task<string?> CheckAsync(AppDbContext db, string? fileName, long sizeBytes, CancellationToken ct)
    {
        var workspaceId = WorkspaceScope.CurrentWorkspaceId ?? WorkspaceScope.DefaultWorkspaceId;
        var ws = await db.NdWorkspaces.AsNoTracking()
            .Where(w => w.Id == workspaceId)
            .Select(w => new { w.MaxDocumentMb, w.MaxStorageMb })
            .FirstOrDefaultAsync(ct);
        if (ws == null) return null;

        var name = string.IsNullOrWhiteSpace(fileName) ? "This file" : $"\"{Path.GetFileName(fileName)}\"";
        if (ws.MaxDocumentMb is int docMb && sizeBytes > docMb * BytesPerMb)
            return $"{name} is {FormatMb(sizeBytes)}, above this business's limit of {docMb} MB per document.";

        if (ws.MaxStorageMb is int storageMb)
        {
            var used = await UsedStorageBytesAsync(db, workspaceId, ct);
            if (used + sizeBytes > storageMb * BytesPerMb)
                return $"{name} would take this business over its {storageMb} MB storage limit ({FormatMb(used)} already used).";
        }
        return null;
    }

    /// <summary>Total size of the workspace's documents still in its library (deleted ones not counted).</summary>
    private static Task<long> UsedStorageBytesAsync(AppDbContext db, Guid workspaceId, CancellationToken ct) =>
        db.StoredDocuments.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == workspaceId && !d.IsHidden)
            .SumAsync(d => d.SizeBytes, ct);

    private static string FormatMb(long bytes) => $"{bytes / (double)BytesPerMb:0.#} MB";
}
