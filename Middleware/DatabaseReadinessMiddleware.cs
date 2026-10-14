namespace CaseManagement.Api.Middleware;

using System.Text.Json;
using CaseManagement.Api.Data;

/// <summary>
/// While the database is being prepared, API calls get a clear, retryable 503 instead of a confusing
/// failure from half-built tables. Non-API paths (<c>/health</c>, <c>/ready</c>, Swagger) are untouched.
/// </summary>
public class DatabaseReadinessMiddleware
{
    private readonly RequestDelegate _next;

    public DatabaseReadinessMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, DatabaseInitializationState state)
    {
        if (!state.IsReady && context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";
            context.Response.Headers["Retry-After"] = "5";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = "The service is starting up. Please retry shortly.",
                status = state.Status.ToString()
            }));
            return;
        }

        await _next(context);
    }
}
