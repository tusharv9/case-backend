namespace CaseManagement.Api.Services.Audit;

using System.Text.RegularExpressions;
using CaseManagement.Api.Models;
using OmniConnect.Outbox;

/// <summary>Who did it and where from, as the request that saved the event knows it. Null fields for background work.</summary>
public sealed record AuditRequestContext(Guid? ActorUserId, string? ActorName, string? SourceIp, string? UserAgent, string? CorrelationId);

/// <summary>
/// The central copy of a Case Management timeline / audit event (<see cref="CaseEvent"/>) for the platform's Audit Service.
/// </summary>
/// <remarks>
/// <para><b>Two places, one event.</b> Case Management keeps its own trail — <c>CaseEvents</c>, which the case timeline and the
/// Case Audit Trail read — and every new row also gets this copy in the <b>same save</b>, through the outbox
/// (<see cref="Data.CaseAuditInterceptor"/>). The event's own id is the copy's <c>EventId</c>, so a retry is stored once.</para>
/// <para><b>Platform vocabulary.</b> Keys are <c>casemanagement.{entity}.{verb}</c> (<c>casemanagement.case.status_changed</c>),
/// categories the platform's own (CRUD, Configuration, ViewDetails), modules the sidebar's labels — so the host's Audit Logs
/// filters work for these rows too. Every key is listed in docs/AUDIT-EVENTS.md.</para>
/// <para><b>Nothing private travels.</b> The details are one plain sentence built from the case number, what was done and the
/// before → after values a person sees on the case (a status, a holder, a level). Note text, customer replies, the case title
/// and the customer are never sent: a central trail is read far more widely than a case.</para>
/// </remarks>
public static class CentralCaseAudit
{
    public const string ServiceName = "CaseManagement";
    public const string SourceApplication = "Case Management";

    private static readonly Dictionary<string, (string Key, string Label)> CaseActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["STATUS_CHANGED"] = ("case.status_changed", "Status changed"),
        ["CASE_RESOLVED"] = ("case.resolved", "Resolved"),
        ["CASE_REOPENED"] = ("case.reopened", "Reopened"),
        ["MANUAL_ESCALATION"] = ("case.escalated", "Escalated"),
        ["AUTO_ESCALATION"] = ("case.auto_escalated", "Escalated automatically"),
        ["BULK_REASSIGN"] = ("case.reassigned", "Reassigned in bulk"),
        ["COLLABORATOR_ADDED"] = ("case.collaborator_added", "Collaborator added"),
        ["MEETING_SCHEDULED"] = ("case.meeting_scheduled", "Meeting scheduled"),
        ["MEETING_RESCHEDULED"] = ("case.meeting_rescheduled", "Meeting rescheduled"),
        ["MEETING_CANCELLED"] = ("case.meeting_cancelled", "Meeting cancelled"),
        ["TICKET_CONVERTED"] = ("case.created_from_ticket", "Created from a ticket"),
        ["EMAIL_CASE_CREATED"] = ("case.created_from_email", "Created from an email"),
        ["EMAIL_REPLY_RECEIVED"] = ("case.email_received", "Customer email received"),
        ["TICKET_ATTACHMENT_REFUSED"] = ("case.attachment_refused", "Attachment refused"),
    };

    private static readonly Dictionary<EventType, (string Key, string Label)> CaseEventTypes = new()
    {
        [EventType.Create] = ("case.created", "Created"),
        [EventType.Assign] = ("case.assigned", "Assigned"),
        [EventType.Note] = ("case.note_added", "Note added"),
        [EventType.Cowork] = ("case.collaboration", "Collaboration"),
        [EventType.Transfer] = ("case.transferred", "Transferred"),
        [EventType.Escalate] = ("case.escalated", "Escalated"),
        [EventType.Resolve] = ("case.resolved", "Resolved"),
        [EventType.Accept] = ("case.accepted", "Accepted"),
        [EventType.Reject] = ("case.rejected", "Rejected"),
        [EventType.Comment] = ("case.comment_added", "Comment added"),
        [EventType.View] = ("case.viewed", "Viewed"),
        [EventType.Update] = ("case.updated", "Updated"),
        [EventType.Close] = ("case.closed", "Closed"),
        [EventType.Other] = ("case.updated", "Updated"),
    };

    /// <summary>Configuration modules: their key prefix and the sidebar page they are changed on.</summary>
    private static readonly Dictionary<string, (string Entity, string Module)> ConfigModules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Teams"] = ("team", "Team Management"),
        ["Configurable Settings"] = ("settings", "Configurable Settings"),
        ["Settings"] = ("settings", "Configurable Settings"),
        ["Departments"] = ("department", "Configurable Settings"),
        ["SlaEscalation"] = ("sla", "SLA Maintenance"),
        ["RoutingEngine"] = ("routing", "Agent Assignment"),
        ["Routing"] = ("routing", "Agent Assignment"),
        ["EmailMaintenance"] = ("email_settings", "Email Configuration"),
        ["EmailReview"] = ("email_review", "Inbox"),
        ["EmailIntake"] = ("email_intake", "Inbox"),
        ["Team Monitoring"] = ("agent", "Team Monitoring"),
    };

    /// <summary>Modules whose own messages may quote a customer (a sender's address): only the action travels.</summary>
    private static readonly HashSet<string> CustomerFacingModules = new(StringComparer.OrdinalIgnoreCase) { "EmailReview", "EmailIntake" };

    /// <summary>The central row for one event. <paramref name="caseNumber"/> names the case it belongs to, when it does.</summary>
    public static AuditEventContract For(CaseEvent e, string? caseNumber, AuditRequestContext request, string appKey)
    {
        var app = string.IsNullOrWhiteSpace(appKey) ? "casemanagement" : appKey.Trim().ToLowerInvariant();
        string key, category, module, details;
        string? entityType, entityId, entityLabel;

        if (e.CaseId is { } caseId)
        {
            var (k, label) = e.ActionType is { Length: > 0 } a && CaseActions.TryGetValue(a, out var byAction) ? byAction
                : CaseEventTypes.TryGetValue(e.EventType, out var byType) ? byType : ("case.updated", "Updated");
            key = k;
            category = e.EventType == EventType.View ? "ViewDetails" : "CRUD";
            module = "Cases";
            entityType = "Case";
            entityId = caseId.ToString();
            entityLabel = caseNumber;
            details = $"{label}: case {caseNumber ?? caseId.ToString()}";
            // The before → after a person sees on the case (a status, a holder, a level) — never a note or a reply.
            if (ShowsValues(e) && (!string.IsNullOrWhiteSpace(e.OldValue) || !string.IsNullOrWhiteSpace(e.NewValue)))
                details += $" ({Clip(e.OldValue) ?? "—"} → {Clip(e.NewValue) ?? "—"})";
            details += ".";
        }
        else
        {
            var known = e.Module is { Length: > 0 } m && ConfigModules.TryGetValue(m, out var cfg) ? cfg : (Entity: Slug(e.Module ?? "configuration"), Module: e.Module ?? "Configurable Settings");
            if (known.Entity == "sla" && (e.EntityName?.Contains("Escalation", StringComparison.OrdinalIgnoreCase) ?? false))
                known = ("escalation_level", "Escalation Maintenance");
            key = $"{known.Entity}.{Verb(e.ActionType)}";
            category = known.Entity is "email_review" or "email_intake" or "agent" ? "CRUD" : "Configuration";
            module = known.Module;
            entityType = e.EntityName ?? e.Module;
            entityId = null;
            entityLabel = e.EntityName;
            details = CustomerFacingModules.Contains(e.Module ?? "")
                ? $"{Humanise(e.ActionType)} ({module})."
                : Clip(e.Message, 500) ?? $"{Humanise(e.ActionType)} ({module}).";
        }

        return new AuditEventContract(
            EventId: e.Id,
            ServiceName: ServiceName,
            Action: $"{app}.{key}",
            OccurredAt: new DateTimeOffset(DateTime.SpecifyKind(e.CreatedAt == default ? DateTime.UtcNow : e.CreatedAt, DateTimeKind.Utc)),
            ActorUserId: request.ActorUserId,
            ActorName: request.ActorName ?? (request.ActorUserId == null ? "System" : null),
            EntityType: entityType,
            EntityId: entityId,
            EntityLabel: entityLabel,
            Details: details,
            SourceIp: request.SourceIp,
            UserAgent: request.UserAgent,
            CorrelationId: request.CorrelationId,
            SourceApplication: SourceApplication,
            Module: module,
            ActionCategory: category);
    }

    /// <summary>Events whose old/new values are workflow values (status, holder, team, level), not free text.</summary>
    private static bool ShowsValues(CaseEvent e) =>
        e.EventType is EventType.Assign or EventType.Transfer or EventType.Escalate or EventType.Update or EventType.Resolve or EventType.Close
        && e.ActionType is not ("EMAIL_REPLY_RECEIVED" or "TICKET_ATTACHMENT_REFUSED");

    private static string Verb(string? actionType) => (actionType ?? "updated").Trim().ToUpperInvariant() switch
    {
        "CREATE" => "created",
        "UPDATE" => "updated",
        "DELETE" => "deleted",
        "UPDATE_CONFIG" => "updated",
        "TOGGLE_STATUS" => "status_changed",
        var other => Slug(other),
    };

    private static string Slug(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    private static string Humanise(string? actionType) =>
        string.IsNullOrWhiteSpace(actionType) ? "Updated" : char.ToUpperInvariant(Slug(actionType)[0]) + Slug(actionType)[1..].Replace('_', ' ');

    private static string? Clip(string? s, int max = 120) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s.Trim() : s[..max].TrimEnd() + "…";
}
