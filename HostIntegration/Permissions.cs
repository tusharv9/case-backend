namespace CaseManagement.Api.HostIntegration;

/// <summary>
/// The permissions Case Management enforces. The Host decides which of its roles carry which permissions (Roles &amp;
/// Permissions, read live from AuthService and translated by <see cref="HostPermissionCatalog"/>); this class is only the
/// vocabulary. No role name appears anywhere in this service: a role called "Admin" or "Agent" grants nothing by its name.
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
    /// See every team's cases. Without it a person sees the cases they hold or collaborate on, plus — with
    /// <see cref="CasesViewTeam"/> — those of the teams they lead (<see cref="ICaseAccessScope"/>).
    /// </summary>
    public const string CasesViewAll = "cases.viewall";

    /// <summary>
    /// See the cases of the teams the person leads. Leading a team (Team Management) is not enough on its own: the Host
    /// must also grant <c>cases:ViewTeam</c>, so who sees a team's cases is decided in the Host, not by the team structure.
    /// </summary>
    public const string CasesViewTeam = "cases.viewteam";

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

    /// <summary>See the email Inbox: emails read from the company mailboxes and the case each became.</summary>
    public const string EmailInboxView = "email.inbox.view";

    /// <summary>Email Configuration: configure mailboxes (address, password, servers) and verified sender domains; re-check an email.</summary>
    public const string EmailManage = "email.manage";

    /// <summary>Review emails from senders who are not registered: approve one email into Tickets, reject it, or mark it suspicious.</summary>
    public const string EmailReview = "email.review";

    /// <summary>When approving an email, also register its sender permanently (future emails from them are verified).</summary>
    public const string EmailSendersRegister = "email.senders.register";

    /// <summary>See Tickets: requests waiting to be assigned, for the mailboxes the person's teams are linked to.</summary>
    public const string TicketsView = "tickets.view";

    /// <summary>Assign a ticket to a team or agent (it becomes a case), or close it without a case.</summary>
    public const string TicketsAssign = "tickets.assign";

    /// <summary>See every mailbox's emails and tickets, not only those linked to the person's teams.</summary>
    public const string TicketsViewAll = "tickets.viewall";

    /// <summary>
    /// See unmasked customer data. Deliberately NOT implied by the "*" wildcard: masking is deny-by-default,
    /// so seeing raw PII must be granted by name.
    /// </summary>
    public const string PiiUnmask = "pii.unmask";

    public const string Everything = "*";

    public static readonly string[] All =
    {
        CasesRead, CasesCreate, CasesWrite, CasesTransfer, CasesResolve, CasesAssign, CasesViewAll, CasesViewTeam, CasesSupervise, CustomersRead, CustomersWrite, ConfigManage,
        TeamsManage, MonitoringView, MonitoringNudge, AuditView, EmailInboxView, EmailManage, TicketsView, TicketsAssign,
        TicketsViewAll, PiiUnmask
    };
}
