namespace CaseManagement.Api.HostIntegration;

/// <summary>
/// The permissions Case Management enforces. The Host App decides which of its roles carry which
/// permissions (via claims or <see cref="HostIntegrationOptions.RolePermissions"/>); this class is the
/// vocabulary shared with the Host team.
/// </summary>
public static class Permissions
{
    public const string CasesRead = "cases.read";
    public const string CasesWrite = "cases.write";

    /// <summary>
    /// Change who holds ANY case (assign, reassign, transfer). Without it a person may do so only for cases they own or that
    /// belong to a team they lead or are an active member of.
    /// </summary>
    public const string CasesAssign = "cases.assign";
    public const string CustomersRead = "customers.read";
    public const string CustomersWrite = "customers.write";
    public const string ConfigManage = "config.manage";
    public const string TeamsManage = "teams.manage";
    public const string MonitoringView = "monitoring.view";
    public const string MonitoringNudge = "monitoring.nudge";
    public const string AuditView = "audit.view";

    /// <summary>
    /// See unmasked customer data. Deliberately NOT implied by the "*" wildcard: masking is deny-by-default,
    /// so seeing raw PII must be granted by name.
    /// </summary>
    public const string PiiUnmask = "pii.unmask";

    public const string Everything = "*";

    public static readonly string[] All =
    {
        CasesRead, CasesWrite, CasesAssign, CustomersRead, CustomersWrite, ConfigManage,
        TeamsManage, MonitoringView, MonitoringNudge, AuditView, PiiUnmask
    };

    /// <summary>
    /// Placeholder role mapping used until the Host team confirms its roles. Replace through
    /// configuration (HostIntegration:RolePermissions); nothing in code needs to change.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> DefaultRolePermissions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent"] = new[] { CasesRead, CasesWrite, CustomersRead, CustomersWrite },
            ["Team Lead"] = new[] { CasesRead, CasesWrite, CasesAssign, CustomersRead, CustomersWrite, MonitoringView, MonitoringNudge, AuditView },
            ["Supervisor"] = new[] { CasesRead, CasesWrite, CasesAssign, CustomersRead, CustomersWrite, MonitoringView, MonitoringNudge, AuditView, TeamsManage },
            ["Admin"] = new[] { Everything, PiiUnmask }
        };
}
