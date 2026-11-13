namespace Reguliq.Api.Infrastructure;

/// <summary>
/// Adds baseline security response headers to every response:
///  - X-Content-Type-Options: nosniff  - stop browsers MIME-sniffing a response into something executable.
///  - X-Frame-Options: DENY  and  CSP frame-ancestors 'none'  - anti-clickjacking; the API/Swagger can't be framed.
///  - Referrer-Policy: no-referrer  - don't leak the URL to other sites.
///
/// HSTS is added separately via UseHsts() in non-Development. Forcing HTTP->HTTPS is intentionally left to the
/// Azure App Service "HTTPS Only" setting rather than app-level redirection, which would loop behind Azure's
/// TLS-terminating proxy and break local HTTP development.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        // Minimal CSP: this is a JSON API, so only the anti-framing directive is needed; it does not restrict
        // Swagger's own scripts/styles (dev only), which are not framed.
        headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        return next(context);
    }
}
