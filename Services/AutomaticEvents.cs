namespace CaseManagement.Api.Services;

using System.Linq.Expressions;
using CaseManagement.Api.Models;

/// <summary>
/// Events the background workers record on a case (SLA reminders, automatic escalations, data reconciliation). The worker
/// stores them under the case's owner or escalation target, because an event must name a user — so "what did this person
/// do" (the agent drawer, the audit trail's person filter, last activity) must leave them out, and the trail shows them as
/// the system's. Recognised by the fixed wording the workers write (SlaMonitorService, DataReconciliationService).
/// </summary>
public static class AutomaticEvents
{
    public const string ActorName = "System (automatic)";

    public static readonly Expression<Func<CaseEvent, bool>> IsPersonal = e =>
        !(e.Message.StartsWith("[Automatic Escalation]")
          || e.Message.StartsWith("SLA reached ")
          || e.Message.Contains(" by data reconciliation ("));

    public static bool IsAutomatic(string? message) =>
        message != null
        && (message.StartsWith("[Automatic Escalation]", StringComparison.Ordinal)
            || message.StartsWith("SLA reached ", StringComparison.Ordinal)
            || message.Contains(" by data reconciliation (", StringComparison.Ordinal));
}
