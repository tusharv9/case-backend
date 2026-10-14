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
    IReadOnlyList<string> Permissions);

/// <summary>A user as listed by the Host's directory (or the local stand-in).</summary>
public sealed record HostUserInfo(
    string ExternalUserId,
    string Name,
    string? Email,
    string? Role,
    bool IsActive);
