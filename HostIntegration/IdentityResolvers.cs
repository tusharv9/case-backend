namespace CaseManagement.Api.HostIntegration;

using System.Security.Claims;
using System.Text.Json;
using CaseManagement.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// STANDALONE (development): the caller names themselves with the <c>X-User-Id</c> header. This is
/// not authentication and exists only so the application runs without a Host App.
/// </summary>
/// <remarks>
/// The user picker sends a user's own id (what <c>GET /api/users</c> lists). For a user first seen through the Host,
/// that is not the external id the user is known by — so it is translated, or every such user (and on a database
/// shared with a Host deployment, that can be every user) is refused with 401 and the standalone app cannot start.
/// </remarks>
public sealed class StandaloneIdentityResolver(AppDbContext? db = null) : IHostIdentityResolver
{
    public const string HeaderName = "X-User-Id";

    public async Task<HostPrincipal?> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var value) || string.IsNullOrWhiteSpace(value))
            return null;

        var id = value.ToString().Trim();
        if (db != null && Guid.TryParse(id, out var localId))
        {
            var external = await db.Users.AsNoTracking().Where(u => u.Id == localId).Select(u => u.ExternalUserId).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(external)) id = external;
        }
        return new HostPrincipal(id, null, null, Array.Empty<string>(), Array.Empty<string>()) { Source = HostIdentitySource.DevelopmentHeader };
    }
}

/// <summary>
/// HYBRID: a request with a bearer token is judged by the token alone (an invalid token is a 401, never a fall-back to
/// the header). Only a request with no token may use the development header, and only when that is allowed.
/// </summary>
public sealed class HybridIdentityResolver(JwtIdentityResolver jwt, StandaloneIdentityResolver devHeader, IOptions<HostIntegrationOptions> options) : IHostIdentityResolver
{
    public Task<HostPrincipal?> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        if (context.Request.Headers.Authorization.Count > 0)
            return jwt.ResolveAsync(context, ct);

        return options.Value.HybridAllowsDevHeader ? devHeader.ResolveAsync(context, ct) : Task.FromResult<HostPrincipal?>(null);
    }
}

/// <summary>
/// HOST: the caller's identity is the validated Host JWT (signature, issuer, audience and expiry are
/// checked by the JWT bearer handler before this runs). Claim names are configuration.
/// </summary>
public sealed class JwtIdentityResolver : IHostIdentityResolver
{
    private readonly HostIntegrationOptions.ClaimSettings _claims;
    private readonly HostCapabilitiesClient? _capabilities;

    public JwtIdentityResolver(IOptions<HostIntegrationOptions> options, HostCapabilitiesClient? capabilities = null)
    {
        _claims = options.Value.Claims;
        _capabilities = capabilities;
    }

    public async Task<HostPrincipal?> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
            return null;

        var id = First(user, _claims.UserId);
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var permissions = Many(user, _claims.Permissions);
        // Exactly "true", as AuthService writes it — never inferred from a role or any other claim.
        var administrator = !string.IsNullOrWhiteSpace(_claims.Administrator) && First(user, _claims.Administrator) == "true";

        // The current Host no longer puts permissions in the token: ask AuthService, which answers live.
        if (_capabilities is { IsConfigured: true })
        {
            var answer = await _capabilities.ForUserAsync(id, ct);
            permissions = answer.Permissions;
            administrator = answer.IsAdministrator;
        }

        return new HostPrincipal(id, First(user, _claims.Name), First(user, _claims.Email), Many(user, _claims.Roles), permissions, administrator);
    }

    private static string? First(ClaimsPrincipal user, string type) =>
        user.Claims.FirstOrDefault(c => c.Type == type)?.Value;

    /// <summary>Reads a claim that may be repeated, comma/space separated, or a JSON array.</summary>
    private static IReadOnlyList<string> Many(ClaimsPrincipal user, string type)
    {
        var values = new List<string>();
        foreach (var claim in user.Claims.Where(c => c.Type == type))
        {
            var raw = claim.Value.Trim();
            if (raw.StartsWith('['))
            {
                try { values.AddRange(JsonSerializer.Deserialize<string[]>(raw) ?? Array.Empty<string>()); continue; }
                catch (JsonException) { /* fall through: treat as plain text */ }
            }
            values.Add(raw);
        }
        return values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
