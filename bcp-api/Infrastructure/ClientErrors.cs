namespace Reguliq.Api.Infrastructure;

/// <summary>
/// The text a controller may return for an exception it caught. Messages our own code wrote for users
/// (thrown from Reguliq.Api code, or our own exception types) are shown as they are, because the web app
/// displays them as guidance. Anything thrown inside the framework, a database driver or a library can carry
/// internal detail, so the user gets a generic message with a reference id, and the full exception is logged
/// under that id.
/// </summary>
public static class ClientErrors
{
    public const string Generic = "Something went wrong. Please try again.";

    public static string Safe(HttpContext context, Exception ex, string? fallback = null)
    {
        if (IsOwnMessage(ex))
            return ex.Message;

        var correlationId = context.TraceIdentifier;
        // Never let logging itself fail while an error is being handled.
        var logger = context.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("ClientErrors");
        logger?.LogError(
            ex,
            "[Hidden from client] {Method} {Path} (correlationId={CorrelationId})",
            context.Request.Method,
            context.Request.Path,
            correlationId);
        return $"{fallback ?? Generic} (Reference: {correlationId})";
    }

    private static bool IsOwnMessage(Exception ex)
    {
        if (string.IsNullOrWhiteSpace(ex.Message)) return false;
        // Our own exception types are written for users.
        if (IsOurs(ex.GetType())) return true;
        // Standard types count only when our code threw them (not when the framework or a driver did).
        return ex is InvalidOperationException or ArgumentException or NotSupportedException or KeyNotFoundException
            && ex.InnerException == null
            && IsOurs(ex.TargetSite?.DeclaringType);
    }

    private static bool IsOurs(Type? type) =>
        type?.Namespace?.StartsWith("Reguliq.Api", StringComparison.Ordinal) == true;
}
