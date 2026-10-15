namespace CaseManagement.Api.HostIntegration;

using Microsoft.Extensions.Options;

/// <summary>
/// Permissions = baseline + this app's capabilities granted in the Host token (translated through
/// <see cref="HostPermissionCatalog"/>) + permissions of each role, from configuration. Roles are matched
/// exactly (case-insensitive) — never by substring. A Host administrator gets everything except
/// <see cref="Permissions.PiiUnmask"/>, which stays deny-by-default and must be granted by name.
/// </summary>
public sealed class ConfiguredPermissionProvider : IPermissionProvider
{
    private readonly HostIntegrationOptions _options;

    public ConfiguredPermissionProvider(IOptions<HostIntegrationOptions> options) => _options = options.Value;

    public IReadOnlySet<string> Resolve(HostPrincipal principal)
    {
        var result = new HashSet<string>(_options.BaselinePermissions, StringComparer.OrdinalIgnoreCase);

        if (principal.IsAdministrator) result.Add(Permissions.Everything);

        // Only this app's own capabilities count. A token entry is never taken at face value, so a stray
        // "*" or "cases.read" string in the claim grants nothing.
        foreach (var permission in HostPermissionCatalog.Translate(principal.Permissions, _options.AppKey))
            result.Add(permission);

        var map = _options.RolePermissions.Count > 0
            ? _options.RolePermissions
            : (IReadOnlyDictionary<string, string[]>)Permissions.DefaultRolePermissions;

        foreach (var role in principal.Roles)
            if (map.TryGetValue(role, out var granted))
                foreach (var permission in granted) result.Add(permission);

        return result;
    }

    public IReadOnlySet<string>? RolesGranting(string permission)
    {
        var map = _options.RolePermissions.Count > 0
            ? _options.RolePermissions
            : (IReadOnlyDictionary<string, string[]>)Permissions.DefaultRolePermissions;

        return map.Where(kv => kv.Value.Contains(permission, StringComparer.OrdinalIgnoreCase) || kv.Value.Contains(Permissions.Everything))
                  .Select(kv => kv.Key)
                  .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>The development stand-in has no roles; it simply gets the configured standalone permissions.</summary>
public sealed class StandalonePermissionProvider : IPermissionProvider
{
    private readonly HostIntegrationOptions _options;

    public StandalonePermissionProvider(IOptions<HostIntegrationOptions> options) => _options = options.Value;

    public IReadOnlySet<string> Resolve(HostPrincipal principal) =>
        new HashSet<string>(_options.StandalonePermissions, StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string>? RolesGranting(string permission) => null;   // no roles in standalone: everyone has the same set
}
