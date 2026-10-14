namespace CaseManagement.Api.Middleware;

using System.Diagnostics;
using System.Text.RegularExpressions;
using CaseManagement.Api.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// Gives every request a correlation id (the caller's, if it is a sane one; otherwise a new one), returns it in the
/// <c>X-Correlation-Id</c> header and adds it to every log line written while the request runs.
/// </summary>
public class CorrelationIdMiddleware
{
    public const string Header = "X-Correlation-Id";
    // Callers choose this value and it ends up in logs: accept only short, boring ids (no log-forging newlines).
    private static readonly Regex Safe = new(@"^[A-Za-z0-9\-_\.]{8,64}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[Header].ToString();
        var id = Safe.IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");
        context.Items[Header] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[Header] = id;
            return Task.CompletedTask;
        });

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
            await _next(context);
    }
}

/// <summary>Defensive response headers. This is a JSON API: nothing it returns should ever be rendered, framed or cached by a browser.</summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _hsts;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityOptions> options, IHostEnvironment env)
    {
        _next = next;
        _hsts = options.Value.EnableHsts && !env.IsDevelopment();
    }

    public Task InvokeAsync(HttpContext context)
    {
        var isDocs = context.Request.Path.StartsWithSegments("/swagger");
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            h["Cross-Origin-Resource-Policy"] = "same-site";
            if (!isDocs)
            {
                h["X-Frame-Options"] = "DENY";
                if (!h.ContainsKey("Content-Security-Policy")) h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            }
            // API data is per-user: never let a shared cache keep it (individual endpoints may set something stricter).
            if (context.Request.Path.StartsWithSegments("/api") && !h.ContainsKey("Cache-Control")) h["Cache-Control"] = "no-store";
            if (_hsts && context.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            return Task.CompletedTask;
        });
        return _next(context);
    }
}

/// <summary>One structured line per API request: method, route, status, duration and who — slow ones as warnings.</summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;
    private readonly int _slowMs;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger, IOptions<SecurityOptions> options)
    {
        _next = next;
        _logger = logger;
        _slowMs = options.Value.SlowRequestMilliseconds;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api")) { await _next(context); return; }   // /health and /ready are polled constantly

        var watch = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            watch.Stop();
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? path.Value;
            var status = context.Response.StatusCode;
            var level = watch.ElapsedMilliseconds >= _slowMs ? LogLevel.Warning : status >= 500 ? LogLevel.Error : LogLevel.Information;
            _logger.Log(level, "{Method} {Route} -> {Status} in {ElapsedMs} ms",
                context.Request.Method, route, status, watch.ElapsedMilliseconds);
        }
    }
}
