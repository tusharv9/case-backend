namespace CaseManagement.Api.HostIntegration;

/// <summary>
/// Who the caller is, as asserted by the Host App (or the development stand-in). Contains only what
/// came from outside; the local user record and permissions are resolved from it.
/// </summary>
public sealed record HostPrincipal(
    string ExternalUserId,
    string? Name,
    string? Email,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    bool IsAdministrator = false)
{
    /// <summary>How the caller proved who they are. Only a Host token may provision a new local user.</summary>
    public HostIdentitySource Source { get; init; } = HostIdentitySource.HostToken;
}

public enum HostIdentitySource
{
    /// <summary>A validated Host-issued JWT.</summary>
    HostToken,

    /// <summary>The <c>X-User-Id</c> development header (no authentication): the standalone demo user picker.</summary>
    DevelopmentHeader
}

/// <summary>A user as listed by the Host's directory (or the local stand-in).</summary>
public sealed record HostUserInfo(
    string ExternalUserId,
    string Name,
    string? Email,
    string? Role,
    bool IsActive);
