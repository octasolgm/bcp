using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard.Integrations;

public sealed class CloudProviderRegistry(IEnumerable<ICloudStorageProvider> providers)
{
    public IReadOnlyList<ICloudStorageProvider> All { get; } = providers.ToList();

    public ICloudStorageProvider? Get(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed record CloudImportResult(
    string FileId,
    string FileName,
    string Result,
    Guid? StoredDocumentId,
    string? Error);

/// <summary>
/// Everything that touches a connected account after sign-in: getting a fresh access token and copying a chosen
/// file into the workspace as a normal Internal or Regulation document (same storage, same record, same
/// "Pending parse" status as an upload).
/// </summary>
public sealed class NdCloudService(
    AppDbContext db,
    CloudProviderRegistry registry,
    CloudSecrets secrets,
    IMemoryCache cache,
    SupabaseStorageService storage,
    NdStoredDocumentUploadService uploadPrep,
    NdRegulationUploadService regulationUpload,
    NdDashboardCacheService dashboardCache,
    ILogger<NdCloudService> logger)
{
    public const long MaxImportBytes = 50L * 1024 * 1024;

    private static string TokenKey(Guid connectionId) => $"nd:cloud-token:{connectionId}";

    /// <summary>A valid access token for the connection. Marks the connection "needs reconnect" if refresh fails.</summary>
    public async Task<string> AccessTokenAsync(NdCloudConnection connection, CancellationToken ct)
    {
        if (cache.TryGetValue<string>(TokenKey(connection.Id), out var cached) && !string.IsNullOrEmpty(cached))
            return cached;

        var provider = registry.Get(connection.Provider)
            ?? throw new CloudProviderException("That storage service is not available.");
        if (connection.Status == CloudConnectionStatuses.NeedsReconnect)
            throw new CloudReconnectRequiredException("This connection needs to be connected again.");

        var refresh = secrets.Decrypt(connection.RefreshTokenEnc);
        if (refresh == null)
        {
            await MarkNeedsReconnectAsync(connection, "The stored sign-in could not be read.", ct);
            throw new CloudReconnectRequiredException("The stored sign-in could not be read. Connect again.");
        }

        try
        {
            var tokens = await provider.RefreshAsync(refresh, ct);
            cache.Set(TokenKey(connection.Id), tokens.AccessToken,
                TimeSpan.FromSeconds(Math.Clamp(tokens.ExpiresInSeconds - 120, 30, 3000)));
            return tokens.AccessToken;
        }
        catch (CloudReconnectRequiredException ex)
        {
            await MarkNeedsReconnectAsync(connection, ex.Message, ct);
            throw;
        }
    }

    public void ForgetToken(Guid connectionId) => cache.Remove(TokenKey(connectionId));

    private async Task MarkNeedsReconnectAsync(NdCloudConnection connection, string reason, CancellationToken ct)
    {
        connection.Status = CloudConnectionStatuses.NeedsReconnect;
        connection.LastError = reason;
        await db.SaveChangesAsync(ct);
    }

    public async Task<CloudImportResult> ImportAsync(
        NdCloudConnection connection,
        string fileId,
        string kind,
        Guid? departmentId,
        Guid userId,
        CancellationToken ct)
    {
        var provider = registry.Get(connection.Provider)
            ?? throw new CloudProviderException("That storage service is not available.");
        var fileName = fileId;
        try
        {
            var token = await AccessTokenAsync(connection, ct);
            var download = await provider.DownloadAsync(token, fileId, MaxImportBytes, ct);
            fileName = download.FileName;

            var hash = Convert.ToHexString(SHA256.HashData(download.Bytes)).ToLowerInvariant();
            var duplicate = kind == "regulation"
                ? await db.StoredDocuments.AnyAsync(d => d.FileHash == hash && !d.IsHidden && d.DocKind == "regulation", ct)
                : await db.StoredDocuments.AnyAsync(
                    d => d.FileHash == hash && !d.IsHidden && (d.DocKind == "document" || d.DocKind == "internal"), ct);
            if (duplicate)
                return await LogAsync(connection, fileId, fileName, kind, null, "duplicate",
                    "This file is already in your documents.", userId, ct);

            Guid storedId;
            if (kind == "regulation")
            {
                var reg = await regulationUpload.UploadAndExtractAsync(
                    download.Bytes, download.FileName, download.ContentType, departmentId, userId, ct);
                storedId = reg.StoredDocumentId ?? reg.Id;
            }
            else
            {
                if (!storage.IsConfigured) throw new CloudProviderException("Document storage is not configured.");
                var prepared = await uploadPrep.PrepareAsync(
                    download.Bytes, download.FileName, download.ContentType, "documents/nd", ct);
                var title = Path.GetFileNameWithoutExtension(download.FileName).Trim();
                var row = NdInternalDocumentFactory.Create(prepared, title, storage.Bucket, userId);
                db.StoredDocuments.Add(row);
                await db.SaveChangesAsync(ct);
                storedId = row.Id;
            }

            dashboardCache.Invalidate();
            connection.LastUsedAt = DateTimeOffset.UtcNow;
            return await LogAsync(connection, fileId, fileName, kind, storedId, "imported", null, userId, ct);
        }
        catch (Exception ex) when (ex is CloudProviderException or InvalidOperationException)
        {
            logger.LogInformation("Cloud import of {File} from {Provider} failed: {Message}", fileName, connection.Provider, ex.Message);
            return await LogAsync(connection, fileId, fileName, kind, null, "failed", ex.Message, userId, ct);
        }
    }

    private async Task<CloudImportResult> LogAsync(
        NdCloudConnection connection,
        string fileId,
        string fileName,
        string kind,
        Guid? storedId,
        string result,
        string? error,
        Guid userId,
        CancellationToken ct)
    {
        db.NdCloudImportLog.Add(new NdCloudImportLog
        {
            ConnectionId = connection.Id,
            Provider = connection.Provider,
            AccountLabel = connection.AccountLabel,
            ProviderFileId = fileId,
            FileName = fileName,
            Kind = kind,
            StoredDocumentId = storedId,
            Result = result,
            Error = error,
            ImportedBy = userId,
        });
        await db.SaveChangesAsync(ct);
        return new CloudImportResult(fileId, fileName, result, storedId, error);
    }
}
