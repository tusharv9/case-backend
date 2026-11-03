namespace CaseManagement.Api.HostIntegration;

/// <summary>
/// What Case Management offers the OmniConnect Host: its permission modules, their capabilities, and the
/// sidebar rows behind them. One table feeds both <c>GET /permissions</c> (what AuthService stores and the
/// Role editor offers) and the translation of a Host token's <c>perms</c> claim into the internal
/// <see cref="Permissions"/> every controller already enforces — so what an administrator can grant and
/// what the server checks cannot drift apart.
/// </summary>
/// <remarks>
/// <para>AuthService issues one shape per granted capability: <c>remote.{appKey}.{module}:{Capability}</c>
/// (e.g. <c>remote.casemanagement.cases:View</c>). Each capability here maps to exactly one internal
/// permission. Nothing else in a token grants anything: an unknown entry is ignored.</para>
///
/// <para><b>Route segments are the remote's page keys.</b> A nav row opens <c>/apps/{appKey}/{Key}</c> and
/// the Host hands <c>Key</c> to the remote as its <c>page</c>; case-frontend's <c>remote/pages.js</c> maps
/// each one to its screen. Change a key here and there together.</para>
/// </remarks>
public static class HostPermissionCatalog
{
    public const string DefaultAppKey = "casemanagement";

    /// <param name="Key">The capability name as the Host writes it after the colon.</param>
    /// <param name="Grants">The internal permission it grants.</param>
    public sealed record Capability(string Key, string DisplayName, string Description, string Grants);

    /// <param name="Key">Stable id; also the route segment and the remote page key.</param>
    /// <param name="RequiredCapability">The capability on this module a role needs to see the row.</param>
    public sealed record NavItem(string Key, string Label, string IconKey, int SortOrder, string RequiredCapability);

    public sealed record Module(
        string Key,
        string DisplayName,
        int SortOrder,
        IReadOnlyList<Capability> Capabilities,
        IReadOnlyList<NavItem> Nav);

    // Icon keys are names from the host's shared icon set (resolveIcon); an unknown one falls back to a box.
    public static readonly IReadOnlyList<Module> Modules =
    [
        new("cases", "Cases", 10,
        [
            new("View", "View cases", "See cases, their history and reports.", Permissions.CasesRead),
            new("Create", "Create cases", "Open new cases.", Permissions.CasesCreate),
            new("Edit", "Work cases", "Update status, add notes, escalate, add co-workers, link cases and attach files; assign within the person's own teams.", Permissions.CasesWrite),
            new("Transfer", "Transfer cases", "Move a case to another team.", Permissions.CasesTransfer),
            new("Resolve", "Resolve and close cases", "Resolve, close, cancel and reopen cases.", Permissions.CasesResolve),
            new("Assign", "Assign any case", "Assign, reassign and transfer cases outside the person's own teams.", Permissions.CasesAssign),
            new("ViewAll", "See every team's cases", "See all cases, not only those of the person's own teams and assignments.", Permissions.CasesViewAll),
            new("Supervise", "Supervise cases", "Bulk reassign and bulk resolve, take over escalated cases, and be asked for help as a Supervisor in Request Swarm.", Permissions.CasesSupervise),
        ],
        [
            // Reports sits below the operational Cases row. Its key stays "dashboard" so existing links and pages.js keep working.
            new("case-management", "Cases", "Briefcase", 10, "View"),
            new("dashboard", "Reports", "PieChart", 20, "View"),
        ]),
        new("customers", "Case Customers", 20,
        [
            new("View", "View case customers", "Look up the customer a case is for.", Permissions.CustomersRead),
            new("Edit", "Add new customers", "Record a new customer who is not in Customer 360.", Permissions.CustomersWrite),
        ],
        []),
        new("teams", "Teams", 30,
        [
            new("Manage", "Manage teams", "Create, edit and staff teams.", Permissions.TeamsManage),
        ],
        [
            new("teams", "Teams", "Users", 10, "Manage"),
        ]),
        new("monitoring", "Team Monitoring", 40,
        [
            new("View", "View team monitoring", "See agent status, workload and SLA risk.", Permissions.MonitoringView),
            new("Nudge", "Nudge agents", "Send an agent a reminder from Team Monitoring.", Permissions.MonitoringNudge),
        ],
        [
            new("team-monitoring", "Team Monitoring", "Activity", 10, "View"),
        ]),
        new("audit", "Case Audit", 50,
        [
            new("View", "View case audit trail", "See who changed which case, and when.", Permissions.AuditView),
        ],
        [
            new("case-audit", "Case Audit Trail", "FileText", 10, "View"),
        ]),
        new("settings", "Case Configuration", 60,
        [
            new("Manage", "Manage case configuration", "Change forms, lists, SLA, routing and assignment settings.", Permissions.ConfigManage),
        ],
        [
            new("cases-sla-routing", "SLA & Routing", "Clock", 10, "Manage"),
            new("configurable-settings", "Case Settings", "Sliders", 20, "Manage"),
        ]),
        new("pii", "Customer Data", 70,
        [
            new("Unmask", "See unmasked customer data", "See customer data without masking.", Permissions.PiiUnmask),
        ],
        []),
    ];

    /// <summary>
    /// Translates the Host's <c>remote.{appKey}.{module}:{Capability}</c> entries into internal permissions.
    /// Matching is exact (case-insensitive) on the whole string; anything that is not one of this app's
    /// capabilities grants nothing.
    /// </summary>
    public static IEnumerable<string> Translate(IEnumerable<string> hostPermissions, string appKey)
    {
        var lookup = Lookup(appKey);
        foreach (var entry in hostPermissions)
            if (lookup.TryGetValue(entry.Trim(), out var granted))
                yield return granted;
    }

    /// <summary>The full Host permission string for a module capability, e.g. remote.casemanagement.cases:View.</summary>
    public static string HostPermission(string appKey, string module, string capability) =>
        $"remote.{appKey}.{module}:{capability}";

    private static Dictionary<string, string> Lookup(string appKey) =>
        Modules
            .SelectMany(m => m.Capabilities.Select(c => (Key: HostPermission(appKey, m.Key, c.Key), c.Grants)))
            .ToDictionary(x => x.Key, x => x.Grants, StringComparer.OrdinalIgnoreCase);
}
