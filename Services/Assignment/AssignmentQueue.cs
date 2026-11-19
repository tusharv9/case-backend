namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Agent Assignment → Needs an agent: the open cases nobody is working, worked out from the data as it is now (no flag is
/// stored, so the list can never disagree with who actually holds each case). A case needs an agent when its owner cannot
/// be the agent working it:
/// <list type="bullet">
/// <item><b>Held by the team lead</b> — the owner is the case's team lead and the lead does not take cases in that team (not a
/// member marked "gets cases"). That is how the lead holds a case under Manual assignment, or when no agent could take it.</item>
/// <item><b>Agent left</b> — the owner is no longer an active person, or no longer an active member of the case's team (and is
/// not its lead): their cases stay with them until someone gives them to another agent.</item>
/// </list>
/// Only cases the caller may see are listed (the same visibility rule as every case list).
/// </summary>
public static class AssignmentQueue
{
    public const string HeldByLead = "HeldByLead";
    public const string AgentLeft = "AgentLeft";

    public static async Task<AssignmentQueueDto> ReadAsync(AppDbContext db, ICaseAccessScope access, AssignmentQueueQuery q, CancellationToken ct = default)
    {
        var finals = new[] { CaseStatus.Resolved, CaseStatus.Closed, CaseStatus.Cancelled };
        var members = db.TeamMembers.AsNoTracking();
        var open = access.Apply(db.Cases.AsNoTracking()).Where(c => !finals.Contains(c.Status));

        // Whether the owner may work the case in its team: an active person who is an active member of it marked "gets cases".
        var waiting = open.Select(c => new
        {
            Case = c,
            OwnerIsLead = c.Team.OwnerId == c.OwnerId,
            OwnerActive = c.Owner.IsActive,
            OwnerMember = members.Any(m => m.TeamId == c.TeamId && m.UserId == c.OwnerId && m.IsActive),
            OwnerTakesCases = members.Any(m => m.TeamId == c.TeamId && m.UserId == c.OwnerId && m.IsActive && m.IsAssignable),
        }).Where(x => (x.OwnerIsLead && !x.OwnerTakesCases) || (!x.OwnerIsLead && (!x.OwnerActive || !x.OwnerMember)));

        var counts = await waiting.GroupBy(x => x.OwnerIsLead && x.OwnerActive)
            .Select(g => new { Held = g.Key, Count = g.Count() }).ToListAsync(ct);
        var heldCount = counts.Where(c => c.Held).Sum(c => c.Count);
        var leftCount = counts.Where(c => !c.Held).Sum(c => c.Count);

        var filtered = waiting;
        if (q.TeamId is { } teamId) filtered = filtered.Where(x => x.Case.TeamId == teamId);
        if (q.Reason == HeldByLead) filtered = filtered.Where(x => x.OwnerIsLead && x.OwnerActive);
        else if (q.Reason == AgentLeft) filtered = filtered.Where(x => !(x.OwnerIsLead && x.OwnerActive));
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLower();
            filtered = filtered.Where(x => x.Case.CaseNumber.ToLower().Contains(s) || x.Case.Title.ToLower().Contains(s)
                || (x.Case.Department != null && x.Case.Department.Name.ToLower().Contains(s)) || x.Case.Team.Name.ToLower().Contains(s));
        }

        var total = await filtered.CountAsync(ct);
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 100);
        var rows = await filtered
            .Select(x => new
            {
                x.Case.Id, x.Case.CaseNumber, x.Case.Title, x.Case.Severity, Status = x.Case.Status,
                Department = x.Case.Department != null ? x.Case.Department.Name : null,
                x.Case.TeamId, TeamName = x.Case.Team.Name, OwnerName = x.Case.Owner.Name, x.Case.CreatedAt,
                Held = x.OwnerIsLead && x.OwnerActive,
                // Waiting since its last assignment (or since it was opened, if it was never reassigned).
                Since = db.CaseEvents.Where(e => e.CaseId == x.Case.Id && e.EventType == EventType.Assign).Max(e => (DateTime?)e.CreatedAt) ?? x.Case.CreatedAt,
            })
            .OrderBy(x => x.Since)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        var teams = await waiting.Select(x => new { x.Case.TeamId, x.Case.Team.Name }).Distinct().OrderBy(t => t.Name).ToListAsync(ct);

        return new AssignmentQueueDto(
            rows.Select(r => new AssignmentQueueItemDto(r.Id, r.CaseNumber, r.Title, r.Severity, r.Status.ToString(), r.Department, r.TeamId, r.TeamName,
                r.Held ? HeldByLead : AgentLeft, r.OwnerName, r.Since)).ToList(),
            total, heldCount + leftCount, heldCount, leftCount,
            teams.Select(t => new NamedOptionDto { Id = t.TeamId, Name = t.Name }).ToList());
    }
}

public sealed record AssignmentQueueQuery(Guid? TeamId, string? Reason, string? Search, int Page = 1, int PageSize = 20);

/// <param name="Reason">HeldByLead or AgentLeft.</param>
/// <param name="HolderName">Who holds the case now: the team lead, or the agent who left.</param>
public sealed record AssignmentQueueItemDto(Guid CaseId, string CaseNumber, string Title, string Priority, string Status, string? DepartmentName,
    Guid TeamId, string TeamName, string Reason, string HolderName, DateTime WaitingSince);

/// <param name="Total">Rows matching the filters (for paging).</param>
/// <param name="NeedsAgentCount">Every case waiting for an agent the caller may see, whatever the filters.</param>
public sealed record AssignmentQueueDto(List<AssignmentQueueItemDto> Items, int Total, int NeedsAgentCount, int HeldByLeadCount, int AgentLeftCount,
    List<NamedOptionDto> Teams);
