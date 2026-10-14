namespace CaseManagement.Api.HostIntegration;

public enum HostIntegrationMode
{
    /// <summary>
    /// No Host App: identity is a development header (<c>X-User-Id</c>) and users are the local
    /// sample users. Development / demo only — see <see cref="HostIntegrationOptions.AllowStandaloneInProduction"/>.
    /// </summary>
    Standalone,

    /// <summary>
    /// The Host App owns identity: callers must present a Host-issued JWT; users are provisioned
    /// from its claims and, optionally, its user directory.
    /// </summary>
    Host
}

/// <summary>
/// Everything that depends on how the Host App identifies people, bound from the "HostIntegration"
/// configuration section. Claim names, token validation, directory endpoint and role→permission
/// mapping are all configuration, so when the Host team decides their contract, adopting it is a
/// settings change, not a code change.
/// </summary>
public class HostIntegrationOptions
{
    public const string SectionName = "HostIntegration";

    /// <summary>Defaults to Standalone in Development; must be set explicitly elsewhere.</summary>
    public HostIntegrationMode? Mode { get; set; }

    /// <summary>
    /// Standalone mode has no real authentication, so outside Development it is refused unless this
    /// is set deliberately (e.g. for a demo deployment while the Host App does not exist yet).
    /// </summary>
    public bool AllowStandaloneInProduction { get; set; }

    public JwtSettings Jwt { get; set; } = new();
    public ClaimSettings Claims { get; set; } = new();
    public DirectorySettings Directory { get; set; } = new();

    /// <summary>
    /// Role (as it appears in the Host's role claim, matched case-insensitively and EXACTLY) → permissions.
    /// "*" grants everything. When empty, <see cref="Permissions.DefaultRolePermissions"/> applies.
    /// </summary>
    public Dictionary<string, string[]> RolePermissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Granted to every authenticated Host user in addition to role permissions.</summary>
    public string[] BaselinePermissions { get; set; } = { Permissions.CasesRead, Permissions.CustomersRead };

    /// <summary>Permissions of the standalone dev user. "*" = everything.</summary>
    public string[] StandalonePermissions { get; set; } = { "*" };

    /// <summary>How long a resolved user (id, active flag, permissions) is cached per process.</summary>
    public int ProvisionCacheSeconds { get; set; } = 60;

    public HostIntegrationMode ResolveMode(bool isDevelopment) =>
        Mode ?? (isDevelopment || AllowStandaloneInProduction ? HostIntegrationMode.Standalone : throw new InvalidOperationException(
            "HostIntegration:Mode must be set to 'Host' or 'Standalone' outside Development."));

    public class JwtSettings
    {
        /// <summary>OIDC authority; signing keys are discovered from it (preferred).</summary>
        public string? Authority { get; set; }

        /// <summary>Explicit metadata (discovery) URL if it is not under the authority.</summary>
        public string? MetadataAddress { get; set; }

        public string? Issuer { get; set; }
        public string? Audience { get; set; }

        /// <summary>HMAC signing key. For local development and automated tests ONLY; use Authority in real deployments.</summary>
        public string? SymmetricKey { get; set; }

        public bool RequireHttpsMetadata { get; set; } = true;
        public int ClockSkewSeconds { get; set; } = 60;
    }

    public class ClaimSettings
    {
        public string UserId { get; set; } = "sub";
        public string Name { get; set; } = "name";
        public string Email { get; set; } = "email";
        public string Roles { get; set; } = "role";
        public string Permissions { get; set; } = "permissions";
    }

    public class DirectorySettings
    {
        /// <summary>Host user-directory API base URL. Empty = no directory; users are provisioned from token claims only.</summary>
        public string? BaseUrl { get; set; }

        public string ListPath { get; set; } = "/users";

        /// <summary>Path of a single user; <c>{id}</c> is replaced by the Host user id.</summary>
        public string GetPath { get; set; } = "/users/{id}";

        /// <summary>Name of the response property holding the array; empty when the response IS the array.</summary>
        public string? ItemsProperty { get; set; }

        /// <summary>Header carrying a service API key for directory calls (optional).</summary>
        public string ApiKeyHeader { get; set; } = "X-Api-Key";
        public string? ApiKey { get; set; }

        /// <summary>Property names in the directory's user objects.</summary>
        public FieldMap Fields { get; set; } = new();

        /// <summary>Background refresh interval; 0 disables it.</summary>
        public int SyncIntervalMinutes { get; set; }

        /// <summary>Deactivate (never delete) local users the directory no longer lists.</summary>
        public bool DeactivateMissing { get; set; }
    }

    public class FieldMap
    {
        public string Id { get; set; } = "id";
        public string Name { get; set; } = "name";
        public string Email { get; set; } = "email";
        public string Role { get; set; } = "role";
        public string IsActive { get; set; } = "isActive";
    }
}
