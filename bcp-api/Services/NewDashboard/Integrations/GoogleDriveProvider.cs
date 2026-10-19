using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.NewDashboard.Integrations;

/// <summary>
/// Google Drive over the Drive v3 REST API with the read-only scope. Google Docs are exported to Word on import;
/// PDF and Word files are downloaded as they are. Needs Integrations:Google:ClientId and ClientSecret.
/// </summary>
public sealed class GoogleDriveProvider(IHttpClientFactory httpFactory, IConfiguration config) : ICloudStorageProvider
{
    public const string SharedWithMeId = "__shared__";
    private const string Scope = "https://www.googleapis.com/auth/drive.readonly";
    private const string PdfMime = "application/pdf";
    private const string WordMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string OldWordMime = "application/msword";
    private const string GoogleDocMime = "application/vnd.google-apps.document";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,200}$", RegexOptions.Compiled);

    private string ClientId => config["Integrations:Google:ClientId"] ?? "";
    private string ClientSecret => config["Integrations:Google:ClientSecret"] ?? "";

    public string Id => "google";
    public string DisplayName => "Google Drive";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    private HttpClient Http => httpFactory.CreateClient("cloud");

    public string BuildAuthorizationUrl(string redirectUri, string state) =>
        "https://accounts.google.com/o/oauth2/v2/auth"
        + $"?client_id={Uri.EscapeDataString(ClientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + "&response_type=code"
        + $"&scope={Uri.EscapeDataString(Scope)}"
        + "&access_type=offline"
        + "&prompt=consent"
        + $"&state={Uri.EscapeDataString(state)}";

    public Task<CloudTokenSet> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        }, ct);

    public Task<CloudTokenSet> RefreshAsync(string refreshToken, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "refresh_token",
        }, ct);

    private async Task<CloudTokenSet> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var res = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var error = JsonString(body, "error");
            if (error == "invalid_grant")
                throw new CloudReconnectRequiredException("Google no longer accepts this sign-in. Connect again.");
            throw new CloudProviderException(
                $"Google sign-in failed: {JsonString(body, "error_description") ?? error ?? res.StatusCode.ToString()}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new CloudTokenSet(
            root.GetProperty("access_token").GetString() ?? "",
            root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
            root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var secs) ? secs : 3000,
            root.TryGetProperty("scope", out var s) ? s.GetString() : null);
    }

    public async Task<CloudAccountInfo> GetAccountAsync(string accessToken, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            "https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress,permissionId)",
            accessToken, ct);
        var user = doc.RootElement.GetProperty("user");
        var email = user.TryGetProperty("emailAddress", out var em) ? em.GetString() : null;
        var name = user.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
        var id = user.TryGetProperty("permissionId", out var pid) ? pid.GetString() : null;
        return new CloudAccountInfo(id ?? email ?? "unknown", email ?? name ?? "Google account");
    }

    public async Task<CloudFolderPage> ListAsync(
        string accessToken, string? folderId, string? pageToken, string? search, CancellationToken ct)
    {
        string query;
        var folderName = "My Drive";
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().Replace("\\", "\\\\").Replace("'", "\\'");
            query = $"name contains '{term}' and trashed = false";
            folderName = "Search results";
        }
        else if (folderId == SharedWithMeId)
        {
            query = "sharedWithMe = true and trashed = false";
            folderName = "Shared with me";
        }
        else
        {
            var parent = string.IsNullOrWhiteSpace(folderId) ? "root" : folderId;
            if (!SafeId.IsMatch(parent)) throw new CloudProviderException("That folder id is not valid.");
            query = $"'{parent}' in parents and trashed = false";
            if (parent != "root") folderName = await FolderNameAsync(parent, accessToken, ct);
        }

        var url = "https://www.googleapis.com/drive/v3/files"
            + $"?q={Uri.EscapeDataString(query)}"
            + "&fields=nextPageToken,files(id,name,mimeType,size,modifiedTime)"
            + "&pageSize=100&orderBy=folder,name"
            + "&supportsAllDrives=true&includeItemsFromAllDrives=true"
            + (string.IsNullOrWhiteSpace(pageToken) ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
        using var doc = await GetJsonAsync(url, accessToken, ct);

        var items = new List<CloudItem>();
        if (doc.RootElement.TryGetProperty("files", out var files))
        {
            foreach (var f in files.EnumerateArray())
            {
                var mime = f.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
                var isFolder = mime == FolderMime;
                long? size = f.TryGetProperty("size", out var sz)
                    && long.TryParse(sz.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
                DateTimeOffset? modified = f.TryGetProperty("modifiedTime", out var mt)
                    && DateTimeOffset.TryParse(mt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                items.Add(new CloudItem(
                    f.GetProperty("id").GetString() ?? "",
                    f.GetProperty("name").GetString() ?? "",
                    isFolder,
                    mime,
                    size,
                    modified,
                    !isFolder && IsImportableMime(mime)));
            }
        }

        var next = doc.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
        return new CloudFolderPage(items, next, folderName);
    }

    public async Task<CloudDownload> DownloadAsync(string accessToken, string fileId, long maxBytes, CancellationToken ct)
    {
        if (!SafeId.IsMatch(fileId)) throw new CloudProviderException("That file id is not valid.");

        using var meta = await GetJsonAsync(
            $"https://www.googleapis.com/drive/v3/files/{fileId}?fields=id,name,mimeType,size&supportsAllDrives=true",
            accessToken, ct);
        var name = meta.RootElement.GetProperty("name").GetString() ?? "document";
        var mime = meta.RootElement.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
        if (!IsImportableMime(mime))
            throw new CloudProviderException($"\"{name}\" is not a PDF or Word file, so it cannot be imported.");
        if (meta.RootElement.TryGetProperty("size", out var sz)
            && long.TryParse(sz.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            && size > maxBytes)
            throw new CloudProviderException($"\"{name}\" is larger than {maxBytes / (1024 * 1024)} MB.");

        string url;
        string contentType;
        if (mime == GoogleDocMime)
        {
            url = $"https://www.googleapis.com/drive/v3/files/{fileId}/export?mimeType={Uri.EscapeDataString(WordMime)}";
            contentType = WordMime;
            if (!name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) name += ".docx";
        }
        else
        {
            url = $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media&supportsAllDrives=true";
            contentType = mime!;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new CloudProviderException(await ErrorMessageAsync(res, ct));
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        if (bytes.LongLength > maxBytes)
            throw new CloudProviderException($"\"{name}\" is larger than {maxBytes / (1024 * 1024)} MB.");
        return new CloudDownload(bytes, name, contentType);
    }

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        try
        {
            using var res = await Http.PostAsync(
                "https://oauth2.googleapis.com/revoke",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Best effort: the stored token is deleted either way.
        }
    }

    private static bool IsImportableMime(string? mime) =>
        mime is PdfMime or WordMime or OldWordMime or GoogleDocMime;

    private async Task<string> FolderNameAsync(string folderId, string accessToken, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync(
                $"https://www.googleapis.com/drive/v3/files/{folderId}?fields=name&supportsAllDrives=true", accessToken, ct);
            return doc.RootElement.GetProperty("name").GetString() ?? "Folder";
        }
        catch (CloudProviderException)
        {
            return "Folder";
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await Http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new CloudReconnectRequiredException("Google no longer accepts this sign-in. Connect again.");
        if (!res.IsSuccessStatusCode) throw new CloudProviderException(await ErrorMessageAsync(res, ct));
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                return $"Google Drive: {m.GetString()}";
        }
        catch (JsonException)
        {
            // fall through to the status code
        }
        return $"Google Drive answered {(int)res.StatusCode}.";
    }

    private static string? JsonString(string body, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(name, out var v) ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
