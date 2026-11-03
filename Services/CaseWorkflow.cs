namespace CaseManagement.Api.Services;

using CaseManagement.Api.Models;

/// <summary>
/// A person may not act on this case right now — it was escalated to someone else (403). Raised by the server whatever
/// the screen offered, so a previous holder cannot change an escalated case through the API either.
/// </summary>
public sealed class CaseLockedException(string message) : Exception(message);

/// <summary>
/// The case status lifecycle, in one place: which status may follow which, and what the Resolve and Escalate workflows may
/// start from. Every endpoint that changes a case's status checks it here, and the case detail tells the screen the same
/// answer (<c>CaseWorkflowDto</c>), so a button and the server cannot disagree.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Open → In Progress only; work on an Open case (a note, a reply, resolving it) moves it there by itself, and it never goes back to Open.</item>
/// <item>In Progress ↔ Waiting on Customer (the SLA clock pauses while waiting).</item>
/// <item>In Progress, Waiting on Customer and Escalated may be resolved or escalated (Escalate is its own workflow, and needs a recipient).</item>
/// <item>Escalated → In Progress / Waiting on Customer, but only by the person it was escalated to or a supervisor.</item>
/// <item>Resolved, Closed and Cancelled are final for status changes: a resolved case comes back only through Reopen.</item>
/// </list>
/// </remarks>
public static class CaseWorkflow
{
    private static readonly Dictionary<CaseStatus, CaseStatus[]> StatusChanges = new()
    {
        [CaseStatus.Open] = new[] { CaseStatus.InProgress },
        [CaseStatus.InProgress] = new[] { CaseStatus.WaitingOnCustomer },
        [CaseStatus.WaitingOnCustomer] = new[] { CaseStatus.InProgress },
        [CaseStatus.Escalated] = new[] { CaseStatus.InProgress, CaseStatus.WaitingOnCustomer },
        [CaseStatus.Resolved] = Array.Empty<CaseStatus>(),
        [CaseStatus.Closed] = Array.Empty<CaseStatus>(),
        [CaseStatus.Cancelled] = Array.Empty<CaseStatus>(),
    };

    /// <summary>A plain status change (the status menu). Resolved and Escalated are reached only through their workflows.</summary>
    public static bool CanChangeStatus(CaseStatus from, CaseStatus to) =>
        StatusChanges.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static IReadOnlyList<CaseStatus> StatusChangesFrom(CaseStatus from) =>
        StatusChanges.TryGetValue(from, out var allowed) ? allowed : Array.Empty<CaseStatus>();

    /// <summary>Open counts: resolving it is work on it, so it first moves to In Progress.</summary>
    public static bool CanResolveFrom(CaseStatus s) =>
        s is CaseStatus.Open or CaseStatus.InProgress or CaseStatus.WaitingOnCustomer or CaseStatus.Escalated;

    public static bool CanEscalateFrom(CaseStatus s) =>
        s is CaseStatus.InProgress or CaseStatus.WaitingOnCustomer or CaseStatus.Escalated;

    public static bool IsFinal(CaseStatus s) => s is CaseStatus.Resolved or CaseStatus.Closed or CaseStatus.Cancelled;

    /// <summary>
    /// Null when <paramref name="actorId"/> may act on the case's workflow (status, resolve, escalate, assign, transfer),
    /// otherwise why not. Only an escalated case is locked: to the person holding it now and to supervisors.
    /// </summary>
    public static string? LockReason(Case c, Guid actorId, bool supervises, string? holderName = null)
    {
        if (c.Status != CaseStatus.Escalated || c.OwnerId == actorId || supervises) return null;
        return $"This case was escalated to {holderName ?? "another person"}. Only they or a supervisor can change it now.";
    }

    public static string Label(CaseStatus s) => s switch
    {
        CaseStatus.InProgress => "In Progress",
        CaseStatus.WaitingOnCustomer => "Waiting on Customer",
        _ => s.ToString(),
    };
}
