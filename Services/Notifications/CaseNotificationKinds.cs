namespace CaseManagement.Api.Services.Notifications;

/// <summary>
/// The notification kinds Case Management raises, declared in <c>GET /permissions</c> on the <c>cases</c> module. AuthService
/// stores them on Resync (Settings → Applications), offers each on Settings → Manage Notifications — where an administrator
/// can switch it off or choose who else is told — and refuses any kind not declared here. Every kind is on by default.
/// </summary>
/// <remarks>
/// <b>The wording people read lives here</b> (Message, Context, SubjectMessage), as templates AuthService renders:
/// <c>{actor}</c> who did it, <c>{verb:…}</c> the coloured action, <c>{record}</c> the case number, and the values
/// <see cref="CaseNotificationDispatcher"/> passes (<c>{caseType}</c>, <c>{severity}</c>, <c>{team}</c>, <c>{status}</c>,
/// <c>{level}</c>, <c>{count}</c>, <c>{reason}</c>). A case is named by its number only — never its title or the
/// customer's name, which travel further in a notification than on the case screen. After changing anything here, resync
/// Case Management.
/// </remarks>
public static class CaseNotificationKinds
{
    public const string Module = "cases";

    public const string Assigned = "case.assigned";
    public const string Reassigned = "case.reassigned";
    public const string NeedsAgent = "case.needs_agent";
    public const string Transferred = "case.transferred";
    public const string StatusChanged = "case.status_changed";
    public const string Resolved = "case.resolved";
    public const string Reopened = "case.reopened";
    public const string NoteAdded = "case.note_added";
    public const string SubcaseCreated = "case.subcase_created";
    public const string ReplySent = "case.reply_sent";
    public const string Escalated = "case.escalated";
    public const string AutoEscalated = "case.auto_escalated";
    public const string Mentioned = "case.mentioned";
    public const string CollaboratorAdded = "case.collaborator_added";
    public const string SwarmRequested = "case.swarm_requested";
    public const string SwarmAccepted = "case.swarm_accepted";
    public const string SwarmDeclined = "case.swarm_declined";
    public const string ReassignedMany = "case.reassigned_many";
    public const string SlaBreached = "case.sla_breached";
    public const string SlaBreachedMany = "case.sla_breached_many";
    public const string SlaReminder = "case.sla_reminder";
    public const string Nudge = "agent.nudge";
    public const string SettingsChanged = "settings.changed";

    /// <summary>The shape AuthService reads (<c>modules[].notifications</c>).</summary>
    public sealed record Declaration(
        string Key, string Label, string Description, string? Subjects,
        bool DefaultEnabled = true, bool DefaultToAdministrators = false,
        string Audience = "Internal", string Severity = "Info",
        string? Message = null, string? Context = null, string? Tone = null, string? Icon = null,
        string? SubjectMessage = null);

    private const string CaseContext = "{caseType} · {severity}";

    public static readonly IReadOnlyList<Declaration> All =
    [
        new(Assigned, "Case assigned to you", "When a new case is given to an agent.", "The case owner",
            Message: "{actor} {verb:assigned} case {record} to {owner}", SubjectMessage: "Case {record} was {verb:assigned} to you",
            Context: CaseContext, Tone: "create", Icon: "Briefcase"),
        new(Reassigned, "Case reassigned to you", "When a case is moved to another agent (one by one or in bulk).", "The new owner",
            Message: "{actor} {verb:reassigned} case {record} to {owner}", SubjectMessage: "{actor} {verb:reassigned} case {record} to you",
            Context: CaseContext, Tone: "create", Icon: "ArrowRight"),
        new(NeedsAgent, "Case needs an agent", "When no agent could take a case, or its owner was deleted, and the team lead holds it.", "The team lead and Supervisors",
            Severity: "Warning", Message: "Case {record} {verb:needs} an agent", Context: "{reason}", Tone: "disable", Icon: "Users"),
        new(Transferred, "Case transferred to your team", "When a case is moved to another team.", "The new owner",
            Message: "{actor} {verb:transferred} case {record} to {team}", SubjectMessage: "{actor} {verb:transferred} case {record} to you",
            Context: CaseContext, Tone: "update", Icon: "ArrowRight"),
        new(StatusChanged, "Case status changed", "When someone else changes the status of a case you own.", "The case owner",
            Message: "{actor} {verb:changed} the status of case {record}", SubjectMessage: "{actor} {verb:changed} the status of your case {record}",
            Context: "Now {status}", Tone: "update", Icon: "RefreshCw"),
        new(Resolved, "Case resolved", "When someone else resolves a case you own.", "The case owner",
            Message: "{actor} {verb:resolved} case {record}", SubjectMessage: "{actor} {verb:resolved} your case {record}",
            Context: CaseContext, Tone: "enable", Icon: "CheckCircle"),
        new(Reopened, "Case reopened", "When a resolved case you own is reopened.", "The case owner",
            Message: "{actor} {verb:reopened} case {record}", SubjectMessage: "{actor} {verb:reopened} your case {record}",
            Context: CaseContext, Tone: "update", Icon: "RotateCcw"),
        new(NoteAdded, "Note added to your case", "When someone else adds a note to a case you own.", "The case owner",
            Message: "{actor} {verb:added} a note to case {record}", SubjectMessage: "{actor} {verb:added} a note to your case {record}",
            Context: CaseContext, Tone: "update", Icon: "FileText"),
        new(SubcaseCreated, "Sub-case created", "When a sub-case is linked to a case you own.", "The case owner",
            Message: "{actor} {verb:linked} a sub-case to case {record}", SubjectMessage: "{actor} {verb:linked} a sub-case to your case {record}",
            Context: CaseContext, Tone: "create", Icon: "Link"),
        new(ReplySent, "Reply sent to the customer", "When someone else replies to the customer on a case you own.", "The case owner",
            Message: "{actor} {verb:replied} to the customer on case {record}", SubjectMessage: "{actor} {verb:replied} to the customer on your case {record}",
            Context: CaseContext, Tone: "update", Icon: "Mail"),
        new(Escalated, "Case escalated to you", "When a case is escalated by hand to the next level.", "The person it is escalated to",
            Severity: "Warning", Message: "{actor} {verb:escalated} case {record} to {level}", SubjectMessage: "{actor} {verb:escalated} case {record} to you ({level})",
            Context: CaseContext, Tone: "security", Icon: "AlertTriangle"),
        new(AutoEscalated, "Case escalated automatically", "When a case passes an escalation level's SLA threshold.", "The level's role holders, the owner and the team lead (as the level says)",
            Severity: "Warning", Message: "Case {record} was {verb:escalated} to {level}", Context: CaseContext, Tone: "security", Icon: "AlertTriangle"),
        new(Mentioned, "You were mentioned", "When someone mentions you in a case note or comment.", "The person mentioned",
            Message: "{actor} {verb:mentioned} {person} in case {record}", SubjectMessage: "{actor} {verb:mentioned} you in case {record}",
            Context: CaseContext, Tone: "update", Icon: "AtSign"),
        new(CollaboratorAdded, "Added as a collaborator", "When you are added to work on a case with its owner.", "The collaborator",
            Message: "{actor} {verb:added} a collaborator to case {record}", SubjectMessage: "{actor} {verb:added} you as a collaborator on case {record}",
            Context: CaseContext, Tone: "create", Icon: "UserPlus"),
        new(SwarmRequested, "Asked to help on a case", "When you are asked to help (swarm) on a case.", "The person asked",
            Message: "{actor} {verb:asked} for help on case {record}", SubjectMessage: "{actor} {verb:asked} you to help on case {record}",
            Context: CaseContext, Tone: "update", Icon: "Users"),
        new(SwarmAccepted, "Help request accepted", "When someone accepts your request for help on a case and joins it.", "The person who asked",
            Message: "{actor} {verb:joined} case {record} to help", SubjectMessage: "{actor} {verb:accepted} your help request on case {record}",
            Context: CaseContext, Tone: "enable", Icon: "Users"),
        new(SwarmDeclined, "Help request declined", "When someone declines your request for help on a case.", "The person who asked",
            Message: "{actor} {verb:declined} the help request on case {record}", SubjectMessage: "{actor} {verb:declined} your help request on case {record}",
            Context: CaseContext, Tone: "disable", Icon: "XCircle"),
        new(ReassignedMany, "Several cases reassigned to you", "When a bulk reassign moves several cases to one agent (one notification for the whole batch).", "The new owner",
            Message: "{actor} {verb:reassigned} {count} cases to {person}", SubjectMessage: "{actor} {verb:reassigned} {count} cases to you",
            Context: "{cases}", Tone: "create", Icon: "ArrowRight"),
        new(SlaBreached, "SLA breached", "When a case you own goes past its SLA.", "The case owner",
            Severity: "Critical", Message: "Case {record} has {verb:breached} its SLA", SubjectMessage: "Your case {record} has {verb:breached} its SLA",
            Context: CaseContext, Tone: "delete", Icon: "Clock"),
        new(SlaBreachedMany, "Several cases breached SLA", "When several of your cases go past their SLA at once (one notification instead of many).", "The case owner",
            Severity: "Critical", Message: "{count} cases have {verb:breached} their SLA", SubjectMessage: "{count} of your cases have {verb:breached} their SLA",
            Tone: "delete", Icon: "Clock"),
        new(SlaReminder, "SLA breach reminder", "A reminder while a case you own stays past its SLA.", "The case owner",
            Severity: "Warning", Message: "Case {record} is {verb:still} past its SLA", SubjectMessage: "Your case {record} is {verb:still} past its SLA",
            Context: "Reminder {reminder}", Tone: "delete", Icon: "Clock"),
        // The supervisor's own words are the card's second line (sent as the body), so this kind declares no context template.
        new(Nudge, "Nudge from a supervisor", "When a supervisor nudges an agent from Team Monitoring.", "The agent nudged",
            Severity: "Warning", Message: "{actor} {verb:nudged} {person}", SubjectMessage: "{actor} {verb:nudged} you",
            Tone: "update", Icon: "Bell"),
        new(SettingsChanged, "Case settings changed", "When someone changes Case Management configuration (case types and similar).", "People who manage Case Settings",
            Message: "{actor} {verb:changed} Case Management settings", Context: "{detail}", Tone: "update", Icon: "Settings"),
    ];

    /// <summary>The old local notification types, as the code still names them, mapped to the kinds above.</summary>
    public static readonly IReadOnlyDictionary<string, string> ByLegacyType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CASE_ASSIGNED"] = Assigned,
        ["CASE_REASSIGNED"] = Reassigned,
        ["CASE_UNASSIGNED"] = NeedsAgent,
        ["CASE_TRANSFERRED"] = Transferred,
        ["CASE_STATUS_CHANGED"] = StatusChanged,
        ["CASE_RESOLVED"] = Resolved,
        ["CASE_REOPENED"] = Reopened,
        ["CASE_NOTE_ADDED"] = NoteAdded,
        ["SUBCASE_CREATED"] = SubcaseCreated,
        ["CUSTOMER_REPLY_SENT"] = ReplySent,
        ["CASE_ESCALATED"] = Escalated,
        ["CASE_AUTOMATICALLY_ESCALATED"] = AutoEscalated,
        ["USER_MENTIONED"] = Mentioned,
        ["CASE_COLLABORATOR_ADDED"] = CollaboratorAdded,
        ["CASE_SWARM_REQUESTED"] = SwarmRequested,
        ["CASE_SWARM_ACCEPTED"] = SwarmAccepted,
        ["CASE_SWARM_DECLINED"] = SwarmDeclined,
        ["Nudge"] = Nudge,
    };
}
