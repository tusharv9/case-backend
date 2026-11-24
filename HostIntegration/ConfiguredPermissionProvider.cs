namespace CaseManagement.Api.HostIntegration;

using Microsoft.Extensions.Options;

/// <summary>
/// Permissions = baseline + this app's capabilities granted in the Host (read live from AuthService, translated through
/// <see cref="HostPermissionCatalog"/>). A Host administrator gets everything except <see cref="Permissions.PiiUnmask"/>,
/// which stays deny-by-default and must be granted by name.
/// </summary>
/// <remarks>
/// <para><b>The Host is the only source.</b> A role's NAME grants nothing: there is no built-in "Agent"/"Admin" table any
/// more (it gave a role called "Admin" everything whatever the Host said). What a role may do is what Roles &amp; Permissions
/// grants it in the Host.</para>
/// <para><see cref="HostIntegrationOptions.RolePermissions"/> remains only as an explicit, empty-by-default operator setting
/// for a deployment whose Host cannot grant capabilities (and for the integration tests); nothing applies unless it is
/// configured.</para>
/// </remarks>
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

        foreach (var role in principal.Roles)
            if (_options.RolePermissions.TryGetValue(role, out var granted))
                foreach (var permission in granted) result.Add(permission);

        return result;
    }

    /// <summary>The roles an explicitly configured role map gives <paramref name="permission"/>; none when no map is set.</summary>
    public IReadOnlySet<string>? RolesGranting(string permission) =>
        _options.RolePermissions
            .Where(kv => kv.Value.Contains(permission, StringComparer.OrdinalIgnoreCase) || kv.Value.Contains(Permissions.Everything))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>HYBRID: Host-token callers get what the Host grants them; demo (development-header) callers get the standalone set.</summary>
public sealed class HybridPermissionProvider(IOptions<HostIntegrationOptions> options) : IPermissionProvider
{
    private readonly ConfiguredPermissionProvider _host = new(options);
    private readonly StandalonePermissionProvider _devHeader = new(options);

    public IReadOnlySet<string> Resolve(HostPrincipal principal) =>
        principal.Source == HostIdentitySource.DevelopmentHeader ? _devHeader.Resolve(principal) : _host.Resolve(principal);

    // Real people: only an explicitly configured role map answers here; the Host directory answers in Host mode.
    public IReadOnlySet<string>? RolesGranting(string permission) => _host.RolesGranting(permission);
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
