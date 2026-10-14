namespace CaseManagement.Api.Middleware;

using System.Text.Json;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

/// <summary>
/// Turns exceptions into consistent JSON errors: <c>{ "error": "…", "correlationId": "…", "errors": { field: [messages] } }</c>
/// ("errors" only for field validation). The mapping is by exception TYPE — never by looking for words in a message — and an
/// unexpected failure never leaks its message or stack to the caller, only a correlation id that finds it in the log.
/// </summary>
public class ExceptionMiddleware
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;

    public ExceptionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, Microsoft.Extensions.Logging.ILogger<ExceptionMiddleware> logger)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller went away. Nothing to report and nobody to report it to.
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                logger.LogError(ex, "Exception after the response started for {Method} {Path}; the connection will be aborted.", context.Request.Method, context.Request.Path);
                context.Abort();
                return;
            }

            var (status, message, errors) = Classify(ex);

            if (status >= 500)
                logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            else
                logger.LogInformation("{Method} {Path} -> {Status}: {Message}", context.Request.Method, context.Request.Path, status, message);

            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";

            var correlationId = context.Items[CorrelationIdMiddleware.Header]?.ToString() ?? string.Empty;
            object body = ex is AssignmentNotAllowedException notAllowed
                ? new { error = message, correlationId, code = notAllowed.ReasonCode }
                : errors == null
                    ? new { error = message, correlationId }
                    : new { error = message, correlationId, errors };
            await context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
        }
    }

    private static (int Status, string Message, Dictionary<string, string[]>? Errors) Classify(Exception ex) => ex switch
    {
        FieldValidationException fv => (400, fv.Message,
            fv.Errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray())),

        KeyNotFoundException => (404, ex.Message, null),

        // Signed in, but not allowed to change who holds this case; or the chosen person cannot hold it (the body carries a "code").
        AssignmentForbiddenException => (403, ex.Message, null),
        AssignmentNotAllowedException => (422, ex.Message, null),
        FileNotFoundException => (404, ex.Message, null),

        // Nobody is signed in (the permission filters answer 403 themselves for "signed in but not allowed").
        UnauthorizedAccessException => (401, ex.Message, null),

        ArgumentException or InvalidOperationException or FormatException => (400, ex.Message, null),

        // Two people changed the same thing, or a unique/foreign-key rule was hit.
        DbUpdateConcurrencyException => (409, "This record was changed by someone else. Reload and try again.", null),
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
            (409, "That already exists.", null),
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } } =>
            (409, "This is still in use or refers to something that no longer exists.", null),

        // Oversized bodies, malformed requests and the like carry their own status.
        BadHttpRequestException bad => (bad.StatusCode, bad.Message, null),

        _ => (500, "An unexpected error occurred. If it keeps happening, quote the correlation id.", null),
    };
}
