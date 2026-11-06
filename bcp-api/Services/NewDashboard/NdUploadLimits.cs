using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// A workspace's upload limits (set by the platform admin on Business workspaces, shown on Business
/// settings): the largest single document, the largest combined size of one multi-file upload, and the
/// total size of its document library. All in MB; null means no workspace limit.
///
/// The server enforces the limits on every upload route. A batch of files sent in one request is checked
/// as a whole. The pages that send several files one request at a time also check the per-upload limit
/// for the whole batch before they start.
/// </summary>
public static class NdUploadLimits
{
    /// <summary>The upload endpoints' own request cap (50 MB); a workspace limit cannot go above it.</summary>
    public const int ServerMaxDocumentMb = 50;

    /// <summary>Limits a new workspace starts with; the platform admin can change or clear them afterwards.</summary>
    public const int DefaultMaxDocumentMb = 10;
    public const int DefaultMaxUploadMb = 20;
    public const int DefaultMaxStorageMb = 50;

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
    public static Task<string?> CheckAsync(AppDbContext db, string? fileName, long sizeBytes, CancellationToken ct) =>
        CheckBatchAsync(db, [(fileName, sizeBytes)], ct);

    /// <summary>Why the files of one multi-file request cannot be uploaded, or null when they all fit.</summary>
    public static Task<string?> CheckAsync(AppDbContext db, IEnumerable<IFormFile> files, CancellationToken ct) =>
        CheckBatchAsync(db, files.Where(f => f.Length > 0).Select(f => ((string?)f.FileName, f.Length)).ToList(), ct);

    /// <summary>Why these files, uploaded together, do not fit the current workspace's limits, or null when
    /// they do: each under the per-document limit, all under the per-upload limit, and the library under its
    /// total storage limit afterwards.</summary>
    public static async Task<string?> CheckBatchAsync(
        AppDbContext db,
        IReadOnlyCollection<(string? FileName, long SizeBytes)> files,
        CancellationToken ct)
    {
        if (files.Count == 0) return null;
        var workspaceId = WorkspaceScope.CurrentWorkspaceId ?? WorkspaceScope.DefaultWorkspaceId;
        var ws = await db.NdWorkspaces.AsNoTracking()
            .Where(w => w.Id == workspaceId)
            .Select(w => new { w.MaxDocumentMb, w.MaxUploadMb, w.MaxStorageMb })
            .FirstOrDefaultAsync(ct);
        if (ws == null) return null;

        if (ws.MaxDocumentMb is int docMb)
        {
            foreach (var (fileName, sizeBytes) in files)
            {
                if (sizeBytes > docMb * BytesPerMb)
                    return $"{DisplayName(fileName)} is {FormatMb(sizeBytes)}, above this business's limit of {docMb} MB per document.";
            }
        }

        var total = files.Sum(f => f.SizeBytes);
        var name = files.Count == 1 ? DisplayName(files.First().FileName) : "These files";
        if (ws.MaxUploadMb is int uploadMb && total > uploadMb * BytesPerMb)
            return $"{name} {(files.Count == 1 ? "is" : "are")} {FormatMb(total)}, above this business's limit of {uploadMb} MB per upload.";

        return ws.MaxStorageMb is int storageMb
            ? await OverStorageMessageAsync(db, workspaceId, name, "would take", total, storageMb, ct)
            : null;
    }

    /// <summary>Why a deleted document cannot be restored, or null when it fits: deleted documents do not
    /// count towards storage, so restoring one adds its size back.</summary>
    public static async Task<string?> CheckRestoreAsync(AppDbContext db, string? fileName, long sizeBytes, CancellationToken ct)
    {
        var workspaceId = WorkspaceScope.CurrentWorkspaceId ?? WorkspaceScope.DefaultWorkspaceId;
        var storageMb = await db.NdWorkspaces.AsNoTracking()
            .Where(w => w.Id == workspaceId)
            .Select(w => w.MaxStorageMb)
            .FirstOrDefaultAsync(ct);
        return storageMb is int mb
            ? await OverStorageMessageAsync(db, workspaceId, DisplayName(fileName), "cannot be restored as it would take", sizeBytes, mb, ct)
            : null;
    }

    private static async Task<string?> OverStorageMessageAsync(
        AppDbContext db, Guid workspaceId, string name, string verb, long addedBytes, int storageMb, CancellationToken ct)
    {
        var used = await UsedStorageBytesAsync(db, workspaceId, ct);
        return used + addedBytes > storageMb * BytesPerMb
            ? $"{name} {verb} this business over its {storageMb} MB storage limit ({FormatMb(used)} already used)."
            : null;
    }

    private static string DisplayName(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? "This file" : $"\"{Path.GetFileName(fileName)}\"";

    /// <summary>Total size of the workspace's documents still in its library (deleted ones not counted).</summary>
    private static Task<long> UsedStorageBytesAsync(AppDbContext db, Guid workspaceId, CancellationToken ct) =>
        db.StoredDocuments.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == workspaceId && !d.IsHidden)
            .SumAsync(d => d.SizeBytes, ct);

    private static string FormatMb(long bytes) => $"{bytes / (double)BytesPerMb:0.#} MB";
}
