namespace Reguliq.Api.Services.NewDashboard.Integrations;

public sealed record CloudTokenSet(string AccessToken, string? RefreshToken, int ExpiresInSeconds, string? Scope);

public sealed record CloudAccountInfo(string Id, string Label);

/// <summary>One entry of a folder listing. <see cref="Importable"/> is false for files we cannot import.</summary>
public sealed record CloudItem(
    string Id,
    string Name,
    bool IsFolder,
    string? MimeType,
    long? SizeBytes,
    DateTimeOffset? ModifiedAt,
    bool Importable);

public sealed record CloudFolderPage(IReadOnlyList<CloudItem> Items, string? NextPageToken, string FolderName);

public sealed record CloudDownload(byte[] Bytes, string FileName, string ContentType);

/// <summary>Thrown when the stored sign-in is no longer valid and the user has to connect again.</summary>
public sealed class CloudReconnectRequiredException(string message) : Exception(message);

/// <summary>Thrown for a problem the user can read as is (file too large, not importable, provider said no).</summary>
public sealed class CloudProviderException(string message) : Exception(message);

/// <summary>
/// One cloud storage service. Read-only: nothing here writes to or deletes from the user's account. Each provider
/// (Google Drive, OneDrive, Dropbox, Box, pCloud) is one small class behind this interface.
/// </summary>
public interface ICloudStorageProvider
{
    /// <summary>Stable id used in addresses and the database: google, microsoft, dropbox, box, pcloud.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>True once the platform has put this provider's client id and secret in configuration.</summary>
    bool IsConfigured { get; }

    string BuildAuthorizationUrl(string redirectUri, string state);

    Task<CloudTokenSet> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct);

    /// <summary>Throws <see cref="CloudReconnectRequiredException"/> when the refresh token no longer works.</summary>
    Task<CloudTokenSet> RefreshAsync(string refreshToken, CancellationToken ct);

    Task<CloudAccountInfo> GetAccountAsync(string accessToken, CancellationToken ct);

    Task<CloudFolderPage> ListAsync(
        string accessToken, string? folderId, string? pageToken, string? search, CancellationToken ct);

    Task<CloudDownload> DownloadAsync(string accessToken, string fileId, long maxBytes, CancellationToken ct);

    /// <summary>Best effort: asks the provider to revoke the token. Never throws.</summary>
    Task RevokeAsync(string token, CancellationToken ct);
}
