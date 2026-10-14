namespace CaseManagement.Api.HostIntegration;

using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;

/// <summary>
/// STANDALONE (development): the caller names themselves with the <c>X-User-Id</c> header. This is
/// not authentication and exists only so the application runs without a Host App.
/// </summary>
public sealed class StandaloneIdentityResolver : IHostIdentityResolver
{
    public const string HeaderName = "X-User-Id";

    public Task<HostPrincipal?> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var value) || string.IsNullOrWhiteSpace(value))
            return Task.FromResult<HostPrincipal?>(null);

        var id = value.ToString().Trim();
        return Task.FromResult<HostPrincipal?>(new HostPrincipal(id, null, null, Array.Empty<string>(), Array.Empty<string>()));
    }
}

/// <summary>
/// HOST: the caller's identity is the validated Host JWT (signature, issuer, audience and expiry are
/// checked by the JWT bearer handler before this runs). Claim names are configuration.
/// </summary>
public sealed class JwtIdentityResolver : IHostIdentityResolver
{
    private readonly HostIntegrationOptions.ClaimSettings _claims;

    public JwtIdentityResolver(IOptions<HostIntegrationOptions> options) => _claims = options.Value.Claims;

    public Task<HostPrincipal?> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
            return Task.FromResult<HostPrincipal?>(null);

        var id = First(user, _claims.UserId);
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult<HostPrincipal?>(null);

        return Task.FromResult<HostPrincipal?>(new HostPrincipal(
            id,
            First(user, _claims.Name),
            First(user, _claims.Email),
            Many(user, _claims.Roles),
            Many(user, _claims.Permissions)));
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
