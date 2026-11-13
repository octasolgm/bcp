using System.Text.Json;

namespace Reguliq.Api.Infrastructure;

/// <summary>
/// Catches otherwise-unhandled exceptions, logs the full detail server-side with a correlation id, and
/// returns a generic JSON error to the client - no stack traces, no internal/driver messages. This closes
/// the leak where an unhandled crash would surface internal detail.
///
/// It does NOT touch the deliberate, user-facing messages controllers already return (e.g. validation
/// errors); those are reviewed case by case, not blanket-changed, since the frontend displays them.
/// </summary>
public sealed class GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var correlationId = context.TraceIdentifier;
            logger.LogError(
                ex,
                "[Unhandled] {Method} {Path} (correlationId={CorrelationId})",
                context.Request.Method,
                context.Request.Path,
                correlationId);

            // If the response already began streaming, we can't replace it - let it surface.
            if (context.Response.HasStarted)
                throw;

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                success = false,
                message = "An unexpected error occurred. Please try again.",
                correlationId,
            }));
        }
    }
}
