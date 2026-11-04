namespace CaseManagement.Api.Services;

using System.Text;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// One-off: takes team membership back to Host users only, so teams can be staffed again from the Host directory.
/// </summary>
/// <remarks>
/// <para>Case Management was first run without a Host, with sample people who are not OmniConnect users. Under the Host
/// they can never sign in, yet they staffed and led every team. For everyone who is NOT a Host user (the Host user ids
/// are given, read from the Host's own database), this:</para>
/// <list type="bullet">
/// <item>removes their team memberships (TeamMembers rows);</item>
/// <item>clears them as team lead (Departments.OwnerId);</item>
/// <item>resets round-robin pointers that point at them (the next case starts from the top of the team);</item>
/// <item>marks them inactive — never deleted — so pickers stop offering them; their history stays.</item>
/// </list>
/// <para>Kept: every team, every user row, every case and who holds it, co-workers on cases, skills and all audit history.
/// A Host user's membership is never touched, so running it again later changes nothing they set up.</para>
/// <para>Dry run by default; <c>--apply</c> makes the change in one transaction with an audit event per team.</para>
/// </remarks>
public sealed class TeamMembershipResetService(AppDbContext context)
{
    public sealed record TeamChange(string Team, List<string> RemovedMembers, string? ClearedLead);

    public sealed class Report
    {
        public bool Applied { get; set; }
        public int HostUsersKnown { get; set; }
        public List<TeamChange> Teams { get; set; } = new();
        public int MembershipsRemoved { get; set; }
        public int LeadsCleared { get; set; }
        public int PointersReset { get; set; }
        public List<string> UsersDeactivated { get; set; } = new();
        public int OpenCasesHeldByDeactivatedUsers { get; set; }
        public int TeamsKept { get; set; }
    }

    public async Task<Report> RunAsync(IReadOnlySet<string> hostUserIds, bool apply, Guid? actorId, CancellationToken ct = default)
    {
        if (hostUserIds.Count == 0)
            throw new InvalidOperationException("No Host user ids were given: refusing to treat every person as a non-Host user.");

        var report = new Report { Applied = apply, HostUsersKnown = hostUserIds.Count };
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var tx = await context.Database.BeginTransactionAsync(ct);

            var users = await context.Users.ToListAsync(ct);
            var nonHost = users.Where(u => u.ExternalUserId == null || !hostUserIds.Contains(u.ExternalUserId)).ToList();
            var nonHostIds = nonHost.Select(u => u.Id).ToHashSet();
            var names = users.ToDictionary(u => u.Id, u => u.Name);

            var memberships = await context.TeamMembers.Where(m => nonHostIds.Contains(m.UserId)).ToListAsync(ct);
            var teams = await context.Teams.OrderBy(d => d.Name).ToListAsync(ct);
            var pointers = await context.TeamAssignmentPointers.Where(p => nonHostIds.Contains(p.LastAssignedUserId)).ToListAsync(ct);

            foreach (var team in teams)
            {
                var removed = memberships.Where(m => m.TeamId == team.Id).Select(m => names[m.UserId]).OrderBy(n => n).ToList();
                var lead = team.OwnerId is { } ownerId && nonHostIds.Contains(ownerId) ? names[ownerId] : null;
                if (removed.Count == 0 && lead == null) continue;
                report.Teams.Add(new TeamChange(team.Name, removed, lead));

                if (apply)
                {
                    if (lead != null) team.OwnerId = null;
                    context.CaseEvents.Add(new CaseEvent
                    {
                        Id = Guid.NewGuid(), CaseId = null, EventType = EventType.Other, CreatedAt = DateTime.UtcNow, UserId = actorId!.Value,
                        Module = "Teams", EntityName = team.Name, ActionType = "MEMBERSHIP_RESET",
                        Message = $"Team '{team.Name}' cleared for staffing from the Host: removed {removed.Count} member(s) who are not Host users"
                                  + (lead != null ? $" and the lead {lead}." : "."),
                        OldValue = string.Join(", ", removed) + (lead != null ? $" | lead: {lead}" : ""),
                        NewValue = null,
                    });
                }
            }

            report.MembershipsRemoved = memberships.Count;
            report.LeadsCleared = report.Teams.Count(t => t.ClearedLead != null);
            report.PointersReset = pointers.Count;
            report.UsersDeactivated = nonHost.Where(u => u.IsActive).Select(u => u.Name).OrderBy(n => n).ToList();
            report.OpenCasesHeldByDeactivatedUsers = await context.Cases.CountAsync(c => nonHostIds.Contains(c.OwnerId)
                && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled, ct);
            report.TeamsKept = teams.Count;

            if (!apply)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            context.TeamMembers.RemoveRange(memberships);
            context.TeamAssignmentPointers.RemoveRange(pointers);
            foreach (var user in nonHost.Where(u => u.IsActive))
            {
                user.IsActive = false;
                user.UpdatedAt = DateTime.UtcNow;
            }
            if (report.UsersDeactivated.Count > 0)
            {
                context.CaseEvents.Add(new CaseEvent
                {
                    Id = Guid.NewGuid(), CaseId = null, EventType = EventType.Other, CreatedAt = DateTime.UtcNow, UserId = actorId!.Value,
                    Module = "Teams", EntityName = "Users", ActionType = "DEACTIVATE_NON_HOST_USERS",
                    Message = $"Marked {report.UsersDeactivated.Count} people inactive who are not OmniConnect Host users (kept, not deleted).",
                    OldValue = string.Join(", ", report.UsersDeactivated), NewValue = null,
                });
            }
            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return report;
    }

    public static string ToText(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Applied ? "APPLIED — team membership reset to Host users only." : "DRY RUN — nothing was changed. Re-run with --apply --actor <email> to make these changes.");
        sb.AppendLine($"Host users known: {r.HostUsersKnown}. Teams kept: {r.TeamsKept} (none deleted).");
        sb.AppendLine($"Memberships removed: {r.MembershipsRemoved}. Team leads cleared: {r.LeadsCleared}. Round-robin pointers reset: {r.PointersReset}.");
        foreach (var t in r.Teams)
            sb.AppendLine($"  - {t.Team}: {t.RemovedMembers.Count} member(s){(t.ClearedLead != null ? $", lead {t.ClearedLead}" : "")}");
        sb.AppendLine($"People marked inactive (not deleted): {r.UsersDeactivated.Count}.");
        sb.AppendLine($"Open cases they still hold (kept as they are; reassign once teams are staffed): {r.OpenCasesHeldByDeactivatedUsers}.");
        return sb.ToString();
    }
}
