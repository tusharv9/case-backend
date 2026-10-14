namespace CaseManagement.Api.HostIntegration;

// ===== PORTS =====
// Case Management never talks to "the Host" directly. It depends on these small interfaces, which
// have a Standalone (development) and a Host (production) implementation. Everything else in the
// application — cases, teams, routing, SLA — sees only a local user id and a permission set.

/// <summary>Works out who is calling from the current HTTP request.</summary>
public interface IHostIdentityResolver
{
    /// <returns>The caller, or null when the request carries no (valid) identity.</returns>
    Task<HostPrincipal?> ResolveAsync(HttpContext context, CancellationToken ct);
}

/// <summary>Lists users known to the Host (used to keep the local projection fresh and to fill user pickers).</summary>
public interface IHostUserDirectory
{
    Task<HostUserInfo?> GetAsync(string externalUserId, CancellationToken ct);
    Task<IReadOnlyList<HostUserInfo>> ListAsync(CancellationToken ct);
}

/// <summary>Decides which permissions a caller has.</summary>
public interface IPermissionProvider
{
    IReadOnlySet<string> Resolve(HostPrincipal principal);

    /// <summary>
    /// The Host roles that carry <paramref name="permission"/> (exact names), or null when permissions do not depend on a
    /// role (Standalone development, where every user gets the configured set) — meaning "everybody".
    /// </summary>
    IReadOnlySet<string>? RolesGranting(string permission);
}

/// <summary>The authenticated caller for the current request, as resolved by <c>HostIdentityMiddleware</c>.</summary>
public interface ICurrentUserAccessor
{
    Guid? LocalUserId { get; }
    HostPrincipal? Principal { get; }
    IReadOnlySet<string> Permissions { get; }
    bool Has(string permission);
}
