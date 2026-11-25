namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Agent Assignment → Needs an agent: the open cases nobody is working, worked out from the data as it is now (no flag is
/// stored, so the list can never disagree with who actually holds each case). A case waits for an agent for one of three reasons:
/// <list type="bullet">
/// <item><b>Held by the team lead</b> — the owner is the case's team lead and the lead does not take cases in that team (not a
/// member marked "gets cases"). That is how the lead holds a case under Manual assignment, or when no agent could take it.</item>
/// <item><b>Agent left</b> — the owner is no longer an active person, or has left the team entirely: not a member of the case's
/// team, not its lead, not the person the case was last escalated to, and not an active member of any other team either. Someone
/// given the case by an escalation, or working it from another team, is still working it.</item>
/// <item><b>Agent unavailable</b> — the owner is still with the team (or holds it by escalation) but is Away: not here, or chose
/// Away (<see cref="AgentPresence"/>). Busy is a choice made while working, so a Busy holder is not waiting.</item>
/// </list>
/// Only cases the caller may see are listed (the same visibility rule as every case list).
/// </summary>
public static class AssignmentQueue
{
    public const string HeldByLead = "HeldByLead";
    public const string AgentLeft = "AgentLeft";
    public const string AgentUnavailable = "AgentUnavailable";

    public static async Task<AssignmentQueueDto> ReadAsync(AppDbContext db, ICaseAccessScope access, AssignmentQueueQuery q, CancellationToken ct = default)
    {
        var finals = new[] { CaseStatus.Resolved, CaseStatus.Closed, CaseStatus.Cancelled };
        var members = db.TeamMembers.AsNoTracking();
        var open = access.Apply(db.Cases.AsNoTracking()).Where(c => !finals.Contains(c.Status));
        var hereSince = DateTime.UtcNow - AgentPresence.Window;

        var facts = open.Select(c => new
        {
            Case = c,
            OwnerActive = c.Owner.IsActive,
            OwnerIsLead = c.Team.OwnerId == c.OwnerId,
            OwnerTakesCases = members.Any(m => m.TeamId == c.TeamId && m.UserId == c.OwnerId && m.IsActive && m.IsAssignable),
            // Still working it: a member of the case's team, the person the case was last escalated to, or active in another team.
            OwnerStays = members.Any(m => m.UserId == c.OwnerId && m.IsActive)
                || db.CaseEscalations.Where(e => e.CaseId == c.Id).OrderByDescending(e => e.CreatedAt).Select(e => e.ToOwnerId).FirstOrDefault() == c.OwnerId,
            // Away: not here (no sign of life within the presence window) or chose Away.
            OwnerAway = c.Owner.LastSeenAt == null || c.Owner.LastSeenAt < hereSince
                || (c.Owner.Status != UserStatus.Available && c.Owner.Status != UserStatus.Busy),
        });
        var waiting = facts.Select(x => new
        {
            x.Case,
            Reason = !x.OwnerActive ? AgentLeft
                : x.OwnerIsLead && !x.OwnerTakesCases ? HeldByLead
                : !x.OwnerIsLead && !x.OwnerStays ? AgentLeft
                : x.OwnerAway ? AgentUnavailable
                : null,
        }).Where(x => x.Reason != null);

        var counts = await waiting.GroupBy(x => x.Reason).Select(g => new { Reason = g.Key, Count = g.Count() }).ToListAsync(ct);
        int CountOf(string reason) => counts.Where(c => c.Reason == reason).Sum(c => c.Count);
        var heldCount = CountOf(HeldByLead);
        var leftCount = CountOf(AgentLeft);
        var unavailableCount = CountOf(AgentUnavailable);

        var filtered = waiting;
        if (q.TeamId is { } teamId) filtered = filtered.Where(x => x.Case.TeamId == teamId);
        if (q.Reason is HeldByLead or AgentLeft or AgentUnavailable) filtered = filtered.Where(x => x.Reason == q.Reason);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLower();
            filtered = filtered.Where(x => x.Case.CaseNumber.ToLower().Contains(s) || x.Case.Title.ToLower().Contains(s)
                || (x.Case.Department != null && x.Case.Department.Name.ToLower().Contains(s)) || x.Case.Team.Name.ToLower().Contains(s));
        }

        var total = await filtered.CountAsync(ct);
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, Paging.MaxPageSize);
        var rows = await filtered
            .Select(x => new
            {
                x.Case.Id, x.Case.CaseNumber, x.Case.Title, x.Case.Severity, Status = x.Case.Status,
                Department = x.Case.Department != null ? x.Case.Department.Name : null,
                x.Case.TeamId, TeamName = x.Case.Team.Name, OwnerName = x.Case.Owner.Name, x.Case.CreatedAt, x.Reason,
                // Waiting since its last assignment (or since it was opened, if it was never reassigned).
                Since = db.CaseEvents.Where(e => e.CaseId == x.Case.Id && e.EventType == EventType.Assign).Max(e => (DateTime?)e.CreatedAt) ?? x.Case.CreatedAt,
            })
            .OrderBy(x => x.Since)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        var teams = await waiting.Select(x => new { x.Case.TeamId, x.Case.Team.Name }).Distinct().OrderBy(t => t.Name).ToListAsync(ct);

        return new AssignmentQueueDto(
            rows.Select(r => new AssignmentQueueItemDto(r.Id, r.CaseNumber, r.Title, r.Severity, r.Status.ToString(), r.Department, r.TeamId, r.TeamName,
                r.Reason!, r.OwnerName, r.Since)).ToList(),
            total, heldCount + leftCount + unavailableCount, heldCount, leftCount,
            teams.Select(t => new NamedOptionDto { Id = t.TeamId, Name = t.Name }).ToList(), unavailableCount);
    }
}

public sealed record AssignmentQueueQuery(Guid? TeamId, string? Reason, string? Search, int Page = 1, int PageSize = 20);

/// <param name="Reason">HeldByLead, AgentLeft or AgentUnavailable.</param>
/// <param name="HolderName">Who holds the case now: the team lead, the agent who left, or the agent who is away.</param>
public sealed record AssignmentQueueItemDto(Guid CaseId, string CaseNumber, string Title, string Priority, string Status, string? DepartmentName,
    Guid TeamId, string TeamName, string Reason, string HolderName, DateTime WaitingSince);

/// <param name="Total">Rows matching the filters (for paging).</param>
/// <param name="NeedsAgentCount">Every case waiting for an agent the caller may see, whatever the filters.</param>
public sealed record AssignmentQueueDto(List<AssignmentQueueItemDto> Items, int Total, int NeedsAgentCount, int HeldByLeadCount, int AgentLeftCount,
    List<NamedOptionDto> Teams, int AgentUnavailableCount = 0);
