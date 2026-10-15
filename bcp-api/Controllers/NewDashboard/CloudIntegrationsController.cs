using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;
using Reguliq.Api.Services.NewDashboard.Integrations;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Cloud storage connections (Google Drive now; OneDrive, Dropbox, Box, pCloud plug in behind the same interface).
/// A workspace admin connects the workspace's own account through the provider's sign-in pop-up; admins and makers
/// then browse it and import PDF/Word files as Internal or Regulation documents. Read-only: nothing is ever written to
/// or deleted from the user's account. Demo accounts get none of this. See docs/storage-provider-integration.
/// </summary>
[ApiController]
[Route("nd/integrations")]
public class CloudIntegrationsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    CloudProviderRegistry registry,
    CloudSecrets secrets,
    NdCloudService cloud,
    NdDemoUserDirectory demoDirectory,
    IConfiguration config,
    IMemoryCache cache) : NdControllerBase
{
    public record ConnectRequest(string? ReturnOrigin);
    public record ImportRequest(List<string>? FileIds, string? Kind, Guid? DepartmentId);

    private const int MaxFilesPerImport = 25;

    private async Task<bool> IsDemoAsync(JwtUser user, CancellationToken ct) =>
        (await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct)).ViewerIsDemo;

    private static object MapConnection(NdCloudConnection c) => new
    {
        id = c.Id,
        provider = c.Provider,
        accountLabel = c.AccountLabel,
        status = c.Status,
        lastError = c.LastError,
        connectedAt = c.ConnectedAt,
        lastUsedAt = c.LastUsedAt,
    };

    private string ApiBase()
    {
        var configured = config["Integrations:PublicApiUrl"]?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(configured)) return configured;
        var host = Request.Host.Value;
        var local = host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("127.0.0.1", StringComparison.Ordinal);
        return $"{(local ? Request.Scheme : "https")}://{host}";
    }

    private string CallbackUrl(ICloudStorageProvider provider) => $"{ApiBase()}/nd/integrations/{provider.Id}/callback";

    private bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        var wanted = origin.Trim().TrimEnd('/');
        return (config["Bcp:CorsOrigins"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(o => string.Equals(o.TrimEnd('/'), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The services this platform offers, with this workspace's connection to each.</summary>
    [HttpGet("providers")]
    public async Task<IActionResult> Providers(CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;
        if (await IsDemoAsync(user, ct)) return Ok(new { success = true, data = Array.Empty<object>() });

        var connections = await db.NdCloudConnections.AsNoTracking().ToListAsync(ct);
        var platformAdmin = IsPlatformAdmin(profile);
        var data = registry.All.Select(p => new
        {
            id = p.Id,
            name = p.DisplayName,
            configured = p.IsConfigured,
            // Only the platform owner needs the address to paste into the provider's console.
            callbackUrl = platformAdmin ? CallbackUrl(p) : null,
            connection = connections.Where(c => c.Provider == p.Id).Select(MapConnection).FirstOrDefault(),
        });
        return Ok(new { success = true, data });
    }

    /// <summary>Starts the sign-in: returns the provider's address, which the browser opens in a pop-up.</summary>
    [HttpPost("providers/{provider}/connect")]
    public async Task<IActionResult> Connect(string provider, [FromBody] ConnectRequest body, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;
        if (await IsDemoAsync(user, ct))
            return StatusCode(403, new { success = false, message = "Cloud storage is not available for demo accounts." });

        var p = registry.Get(provider);
        if (p == null) return NotFound(new { success = false, message = "Unknown storage service." });
        if (!p.IsConfigured)
            return BadRequest(new { success = false, message = $"{p.DisplayName} has not been set up by the platform yet." });
        if (!IsAllowedOrigin(body?.ReturnOrigin))
            return BadRequest(new { success = false, message = "This web address is not allowed to start a connection." });

        var tenantId = WorkspaceScope.CurrentWorkspaceId ?? profile.TenantId ?? WorkspaceScope.DefaultWorkspaceId;
        var state = secrets.SignState(new CloudOAuthState(
            tenantId,
            profile.Id,
            p.Id,
            DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
            CloudSecrets.NewNonce(),
            body!.ReturnOrigin!.Trim().TrimEnd('/')));
        return Ok(new { success = true, data = new { url = p.BuildAuthorizationUrl(CallbackUrl(p), state) } });
    }

    /// <summary>Where the provider sends the browser back after sign-in. No login token here: the signed state says who.</summary>
    [HttpGet("{provider}/callback")]
    public async Task<IActionResult> Callback(
        string provider,
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        var verified = secrets.VerifyState(state);
        var p = registry.Get(provider);
        if (verified == null || p == null || !string.Equals(verified.Provider, p.Id, StringComparison.OrdinalIgnoreCase))
            return ResultPage(null, provider, false, "This sign-in link has expired or is not valid. Close this window and try again.");

        // One use only: a replayed link is refused.
        if (cache.TryGetValue("nd:cloud-nonce:" + verified.Nonce, out _))
            return ResultPage(verified.ReturnOrigin, provider, false, "This sign-in link was already used. Close this window and try again.");
        cache.Set("nd:cloud-nonce:" + verified.Nonce, true, TimeSpan.FromMinutes(15));

        if (!string.IsNullOrEmpty(error))
            return ResultPage(verified.ReturnOrigin, provider, false,
                error == "access_denied" ? "Access was not allowed, so nothing was connected." : $"{p.DisplayName} did not connect ({error}).");
        if (string.IsNullOrWhiteSpace(code))
            return ResultPage(verified.ReturnOrigin, provider, false, "No sign-in code was returned. Try again.");

        try
        {
            var tokens = await p.ExchangeCodeAsync(code, CallbackUrl(p), ct);
            var account = await p.GetAccountAsync(tokens.AccessToken, ct);

            var row = await db.NdCloudConnections.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == verified.TenantId && c.Provider == p.Id, ct);
            var refresh = tokens.RefreshToken;
            if (string.IsNullOrEmpty(refresh) && row != null) refresh = secrets.Decrypt(row.RefreshTokenEnc);
            if (string.IsNullOrEmpty(refresh))
                return ResultPage(verified.ReturnOrigin, provider, false,
                    $"{p.DisplayName} did not give long-term access. Remove this app at your account's connected apps and try again.");

            if (row == null)
            {
                row = new NdCloudConnection { Provider = p.Id, TenantId = verified.TenantId };
                db.NdCloudConnections.Add(row);
            }
            row.AccountId = account.Id;
            row.AccountLabel = account.Label;
            row.RefreshTokenEnc = secrets.Encrypt(refresh);
            row.Scopes = tokens.Scope;
            row.Status = CloudConnectionStatuses.Connected;
            row.LastError = null;
            row.ConnectedBy = verified.UserId;
            row.ConnectedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            cloud.ForgetToken(row.Id);
            return ResultPage(verified.ReturnOrigin, provider, true, $"Connected to {account.Label}.");
        }
        catch (Exception ex) when (ex is CloudProviderException or CloudReconnectRequiredException or HttpRequestException)
        {
            return ResultPage(verified.ReturnOrigin, provider, false, ex.Message);
        }
    }

    [HttpDelete("connections/{id:guid}")]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken ct)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;
        if (await IsDemoAsync(user, ct)) return StatusCode(403, new { success = false, message = "Not available for demo accounts." });

        var row = await db.NdCloudConnections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row == null) return NotFound(new { success = false, message = "Connection not found." });

        var p = registry.Get(row.Provider);
        var token = secrets.Decrypt(row.RefreshTokenEnc);
        if (p != null && token != null) await p.RevokeAsync(token, ct);
        db.NdCloudConnections.Remove(row);
        await db.SaveChangesAsync(ct);
        cloud.ForgetToken(id);
        return Ok(new { success = true });
    }

    /// <summary>One folder of the connected account (files and sub folders), 100 at a time.</summary>
    [HttpGet("connections/{id:guid}/browse")]
    public async Task<IActionResult> Browse(
        Guid id,
        [FromQuery] string? folder,
        [FromQuery] string? pageToken,
        [FromQuery] string? q,
        CancellationToken ct)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;
        if (await IsDemoAsync(user, ct)) return StatusCode(403, new { success = false, message = "Not available for demo accounts." });

        var row = await db.NdCloudConnections.FirstOrDefaultAsync(c => c.Id == id, ct);
        var p = row == null ? null : registry.Get(row.Provider);
        if (row == null || p == null) return NotFound(new { success = false, message = "Connection not found." });

        try
        {
            var token = await cloud.AccessTokenAsync(row, ct);
            var page = await p.ListAsync(token, folder, pageToken, q, ct);
            return Ok(new { success = true, data = page });
        }
        catch (CloudReconnectRequiredException ex)
        {
            return Conflict(new { success = false, needsReconnect = true, message = ex.Message });
        }
        catch (CloudProviderException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>Copies the chosen files into the workspace. Each one is reported as imported, duplicate or failed.</summary>
    [HttpPost("connections/{id:guid}/import")]
    public async Task<IActionResult> Import(Guid id, [FromBody] ImportRequest body, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;
        if (await IsDemoAsync(user, ct)) return StatusCode(403, new { success = false, message = "Not available for demo accounts." });

        var kind = (body?.Kind ?? "internal").Trim().ToLowerInvariant();
        if (kind is not ("internal" or "regulation"))
            return BadRequest(new { success = false, message = "Choose Internal or Regulation." });
        var ids = (body?.FileIds ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToList();
        if (ids.Count == 0) return BadRequest(new { success = false, message = "Choose at least one file." });
        if (ids.Count > MaxFilesPerImport)
            return BadRequest(new { success = false, message = $"Import at most {MaxFilesPerImport} files at a time." });

        var row = await db.NdCloudConnections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row == null) return NotFound(new { success = false, message = "Connection not found." });

        var results = new List<CloudImportResult>();
        try
        {
            foreach (var fileId in ids)
                results.Add(await cloud.ImportAsync(row, fileId, kind, body?.DepartmentId, profile.Id, ct));
        }
        catch (CloudReconnectRequiredException ex)
        {
            return Conflict(new { success = false, needsReconnect = true, message = ex.Message, data = results });
        }

        return Ok(new
        {
            success = true,
            data = results,
            summary = new
            {
                imported = results.Count(r => r.Result == "imported"),
                duplicate = results.Count(r => r.Result == "duplicate"),
                failed = results.Count(r => r.Result == "failed"),
            },
        });
    }

    /// <summary>Who imported what from where: the workspace admin's audit view.</summary>
    [HttpGet("import-log")]
    public async Task<IActionResult> ImportLog([FromQuery] int take = 50, CancellationToken ct = default)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;
        if (await IsDemoAsync(user, ct)) return StatusCode(403, new { success = false, message = "Not available for demo accounts." });

        var rows = await db.NdCloudImportLog.AsNoTracking()
            .OrderByDescending(l => l.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);
        var names = await LoadProfileNamesAsync(db, rows.Select(r => r.ImportedBy), ct);
        return Ok(new
        {
            success = true,
            data = rows.Select(r => new
            {
                id = r.Id,
                provider = r.Provider,
                accountLabel = r.AccountLabel,
                fileName = r.FileName,
                kind = r.Kind,
                result = r.Result,
                error = r.Error,
                by = ProfileName(names, r.ImportedBy),
                createdAt = r.CreatedAt,
            }),
        });
    }

    /// <summary>
    /// The pop-up's last page: tells the app that opened it how it went, then closes itself. Without an opener (the
    /// pop-up was blocked and the page opened in the same tab) it sends the browser back to the Integrations page.
    /// </summary>
    private ContentResult ResultPage(string? returnOrigin, string provider, bool ok, string message)
    {
        var payload = JsonSerializer.Serialize(new { type = "cloud-connect", provider, ok, message });
        var origin = JsonSerializer.Serialize(returnOrigin ?? "");
        var back = JsonSerializer.Serialize(
            string.IsNullOrEmpty(returnOrigin)
                ? ""
                : $"{returnOrigin}/nd/admin/integrations?{(ok ? "connected" : "error")}={Uri.EscapeDataString(ok ? provider : message)}");
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Comply Solutions</title></head>
            <body style="font-family:system-ui,sans-serif;max-width:32rem;margin:15vh auto;padding:0 1rem;text-align:center">
            <h1 style="font-size:1.25rem">{{(ok ? "Connected" : "Not connected")}}</h1>
            <p>{{WebUtility.HtmlEncode(message)}}</p>
            <p style="color:#666">You can close this window.</p>
            <script>
            (function () {
              var result = {{payload}}, origin = {{origin}}, back = {{back}};
              try {
                if (window.opener && origin) { window.opener.postMessage(result, origin); window.close(); }
                else if (back) { window.location.href = back; }
              } catch (e) {}
            })();
            </script></body></html>
            """;
        return new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = 200 };
    }
}
