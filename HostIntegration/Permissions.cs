namespace CaseManagement.Api.HostIntegration;

/// <summary>
/// The permissions Case Management enforces. The Host App decides which of its roles carry which
/// permissions (via claims or <see cref="HostIntegrationOptions.RolePermissions"/>); this class is the
/// vocabulary shared with the Host team.
/// </summary>
public static class Permissions
{
    public const string CasesRead = "cases.read";

    /// <summary>Open a new case.</summary>
    public const string CasesCreate = "cases.create";

    /// <summary>Work a case: status, notes, escalation, co-workers, links, attachments, assignment within one's own teams.</summary>
    public const string CasesWrite = "cases.write";

    /// <summary>Move a case to another team.</summary>
    public const string CasesTransfer = "cases.transfer";

    /// <summary>Resolve, close, cancel or reopen a case.</summary>
    public const string CasesResolve = "cases.resolve";

    /// <summary>
    /// Change who holds ANY case (assign, reassign, transfer). Without it a person may do so only for cases they own or that
    /// belong to a team they lead or are an active member of.
    /// </summary>
    public const string CasesAssign = "cases.assign";

    /// <summary>
    /// See every team's cases. Without it a person sees the cases they hold or take part in, and those of the teams
    /// they lead or are an active member of (<see cref="ICaseAccessScope"/>).
    /// </summary>
    public const string CasesViewAll = "cases.viewall";

    /// <summary>
    /// Supervise cases: bulk reassign and bulk resolve, act on an escalated case someone else holds, and be offered as a
    /// Supervisor in Request Swarm. Host roles granted <c>cases:Supervise</c> are the platform's Supervisors.
    /// </summary>
    public const string CasesSupervise = "cases.supervise";
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
        CasesRead, CasesCreate, CasesWrite, CasesTransfer, CasesResolve, CasesAssign, CasesViewAll, CasesSupervise, CustomersRead, CustomersWrite, ConfigManage,
        TeamsManage, MonitoringView, MonitoringNudge, AuditView, PiiUnmask
    };

    /// <summary>
    /// Placeholder role mapping used until the Host team confirms its roles. Replace through
    /// configuration (HostIntegration:RolePermissions); nothing in code needs to change.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> DefaultRolePermissions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent"] = new[] { CasesRead, CasesCreate, CasesWrite, CasesResolve, CustomersRead, CustomersWrite },
            ["Team Lead"] = new[] { CasesRead, CasesCreate, CasesWrite, CasesTransfer, CasesResolve, CasesAssign, CustomersRead, CustomersWrite, MonitoringView, MonitoringNudge, AuditView },
            ["Supervisor"] = new[] { CasesRead, CasesCreate, CasesWrite, CasesTransfer, CasesResolve, CasesAssign, CasesViewAll, CasesSupervise, CustomersRead, CustomersWrite, MonitoringView, MonitoringNudge, AuditView, TeamsManage },
            ["Admin"] = new[] { Everything, PiiUnmask }
        };
}
