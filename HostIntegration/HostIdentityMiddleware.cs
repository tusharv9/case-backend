namespace CaseManagement.Api.HostIntegration;

using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

/// <summary>
/// Establishes who is calling for every /api request, using whichever <see cref="IHostIdentityResolver"/>
/// the current mode wires in, then publishes the result (local user id + permissions) for the rest of the
/// application. It replaces the old "does this GUID exist" check, which was not authentication.
/// </summary>
public sealed class HostIdentityMiddleware
{
    private readonly RequestDelegate _next;

    public HostIdentityMiddleware(RequestDelegate next) => _next = next;

    private sealed record CachedUser(Guid LocalId, bool IsActive);

    /// <summary>Anonymous: tells a signed-out UI which ways of signing in this deployment accepts.</summary>
    public const string SessionOptionsPath = "/api/session/options";

    public async Task InvokeAsync(
        HttpContext context,
        IHostIdentityResolver resolver,
        IPermissionProvider permissionProvider,
        IUserProjectionService projection,
        IMemoryCache cache,
        IOptions<HostIntegrationOptions> options,
        IHostEnvironment env)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var settings = options.Value;
        var mode = settings.ResolveMode(env.IsDevelopment());

        // Before anyone is signed in, the UI must learn how it may sign in.
        if (HttpMethods.IsGet(context.Request.Method) &&
            context.Request.Path.Equals(SessionOptionsPath, StringComparison.OrdinalIgnoreCase))
        {
            context.Items[PermissionAuthorizationFilter.OpenBeforeSignInKey] = true;
            await _next(context);
            return;
        }

        // Development header accepted: the demo user picker has to list users before anyone is "signed in".
        // A request that carries a token is identified normally instead.
        if (settings.AcceptsDevHeader(mode) &&
            context.Request.Headers.Authorization.Count == 0 &&
            HttpMethods.IsGet(context.Request.Method) &&
            context.Request.Path.Equals("/api/users", StringComparison.OrdinalIgnoreCase))
        {
            context.Items[PermissionAuthorizationFilter.OpenBeforeSignInKey] = true;
            await _next(context);
            return;
        }

        HostPrincipal? principal;
        try
        {
            principal = await resolver.ResolveAsync(context, context.RequestAborted);
        }
        catch (HostAccessException ex)
        {
            await Reject(context, ex.Status, ex.Message);
            return;
        }
        if (principal == null)
        {
            await Reject(context, StatusCodes.Status401Unauthorized,
                mode == HostIntegrationMode.Standalone ? "Missing or invalid X-User-Id header." : "Authentication required.");
            return;
        }

        var cacheKey = "host-user:" + principal.ExternalUserId;
        if (!cache.TryGetValue(cacheKey, out CachedUser? user) || user == null)
        {
            var projected = await projection.EnsureAsync(principal, provisionIfMissing: principal.Source == HostIdentitySource.HostToken, context.RequestAborted);
            if (projected == null)
            {
                await Reject(context, StatusCodes.Status401Unauthorized, "User not found or unauthorized.");
                return;
            }

            user = new CachedUser(projected.LocalId, projected.IsActive);
            if (settings.ProvisionCacheSeconds > 0)
                cache.Set(cacheKey, user, TimeSpan.FromSeconds(settings.ProvisionCacheSeconds));
        }

        if (!user.IsActive)
        {
            await Reject(context, StatusCodes.Status403Forbidden, "This user account is inactive.");
            return;
        }

        context.Items[CurrentUserAccessor.LocalUserIdKey] = user.LocalId;
        context.Items[CurrentUserAccessor.PrincipalKey] = principal;
        context.Items[CurrentUserAccessor.PermissionsKey] = permissionProvider.Resolve(principal);

        await _next(context);
    }

    private static async Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        if (status == StatusCodes.Status401Unauthorized)
            context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }));
    }
}
