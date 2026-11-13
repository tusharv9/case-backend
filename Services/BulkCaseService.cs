namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Teams;
using CaseManagement.Api.Repositories;
using Microsoft.EntityFrameworkCore;

public interface IBulkCaseService
{
    /// <summary>One page of the cases the caller may reassign or resolve in bulk.</summary>
    Task<PagedResponseDto<BulkCaseRowDto>> SearchAsync(BulkCaseQueryDto query, CancellationToken ct = default);

    /// <summary>Who can receive cases of <paramref name="departmentId"/>: the lead and members of the team it is assigned to.</summary>
    Task<IReadOnlyList<BulkAssigneeDto>> AssigneesAsync(Guid departmentId, CancellationToken ct = default);

    Task<BulkResultDto> ReassignAsync(BulkReassignRequestDto request, Guid actorId, CancellationToken ct = default);
    Task<BulkResultDto> ResolveAsync(BulkResolveRequestDto request, Guid actorId, CancellationToken ct = default);
}

/// <summary>
/// Supervisor bulk operations (Bulk Reassign, Bulk Resolve), behind <c>cases:Supervise</c>.
/// </summary>
/// <remarks>
/// <para><b>Each case on its own.</b> Every selected case is checked and saved separately, in its own transaction: one that
/// cannot be changed (not visible to the caller, already resolved, assignee at capacity…) is reported with its reason and
/// never stops — or undoes — the others. Nothing is skipped silently: the result lists every case asked for.</para>
/// <para><b>Department first.</b> A team can serve several departments and a department several teams, so the target
/// department is chosen explicitly; the assignee must lead or belong to the team it is assigned to, and the case moves to that
/// team and that department together.</para>
/// <para><b>No second rule book.</b> Bulk Resolve runs the single-case resolution (<see cref="ICaseService.ResolveCaseAsync(Guid, ResolveCaseDto, bool)"/>)
/// for each case, and Bulk Reassign the same assignment eligibility (capacity, manual-assignment flags) as Assign.</para>
/// </remarks>
public class BulkCaseService(
    AppDbContext context,
    ICaseService cases,
    ICaseAccessScope access,
    IAssignmentEligibilityService eligibility,
    INotificationService notifications,
    ILogger<BulkCaseService> logger) : IBulkCaseService
{
    public const int MaxCasesPerRequest = 200;

    public async Task<PagedResponseDto<BulkCaseRowDto>> SearchAsync(BulkCaseQueryDto query, CancellationToken ct = default)
    {
        var q = access.Apply(context.Cases.AsNoTracking())
            .Where(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled);

        if (!string.IsNullOrWhiteSpace(query.Status) && Enum.TryParse<CaseStatus>(query.Status.Replace(" ", ""), true, out var status))
            q = q.Where(c => c.Status == status);
        if (query.DepartmentId.HasValue) q = q.Where(c => c.DepartmentId == query.DepartmentId.Value);
        if (query.TeamId.HasValue) q = q.Where(c => c.TeamId == query.TeamId.Value);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = SqlSearchPattern.Contains(query.Search.Trim());
            q = q.Where(c => EF.Functions.ILike(c.CaseNumber, pattern) || EF.Functions.ILike(c.Title, pattern) || EF.Functions.ILike(c.Owner.Name, pattern));
        }

        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var page = Math.Max(1, query.Page);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new BulkCaseRowDto
            {
                Id = c.Id, CaseNumber = c.CaseNumber, Title = c.Title, Status = c.Status.ToString(), Priority = c.Severity,
                DepartmentId = c.DepartmentId, DepartmentName = c.Department != null ? c.Department.Name : null,
                TeamId = c.TeamId, TeamName = c.Team.Name, OwnerId = c.OwnerId, OwnerName = c.Owner.Name,
                EscalationLevel = c.EscalationLevel, CreatedAt = c.CreatedAt,
            })
            .ToListAsync(ct);

        // Both operations list the same open cases; each case's own rules decide on submit and failures are reported per case.
        return new PagedResponseDto<BulkCaseRowDto> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public async Task<IReadOnlyList<BulkAssigneeDto>> AssigneesAsync(Guid departmentId, CancellationToken ct = default)
    {
        var teams = await AssignedTeamAsync(departmentId, ct);
        var teamIds = teams.Select(t => t.Id).ToList();

        var members = await context.TeamMembers.AsNoTracking()
            .Where(m => teamIds.Contains(m.TeamId) && m.IsActive && m.CanBeManuallyAssigned && m.User.IsActive)
            .Select(m => new { m.UserId, m.User.Name, m.User.Email, m.MemberRole, m.TeamId })
            .ToListAsync(ct);

        var rows = new List<BulkAssigneeDto>();
        foreach (var team in teams)
        {
            if (team.OwnerId.HasValue && team.Owner is { IsActive: true } lead)
                rows.Add(new BulkAssigneeDto { UserId = lead.Id, Name = lead.Name, Email = lead.Email, Role = "Team Lead", IsTeamLead = true, TeamId = team.Id, TeamName = team.Name });
            foreach (var m in members.Where(m => m.TeamId == team.Id && m.UserId != team.OwnerId))
                rows.Add(new BulkAssigneeDto { UserId = m.UserId, Name = m.Name, Email = m.Email, Role = string.IsNullOrWhiteSpace(m.MemberRole) ? "Agent" : m.MemberRole, TeamId = team.Id, TeamName = team.Name });
        }

        var ids = rows.Select(r => r.UserId).Distinct().ToList();
        var open = await context.Cases.AsNoTracking()
            .Where(c => ids.Contains(c.OwnerId) && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled)
            .GroupBy(c => c.OwnerId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        foreach (var r in rows) r.OpenCases = open.GetValueOrDefault(r.UserId);

        return rows.OrderBy(r => r.TeamName, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.IsTeamLead)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The one active team the department is assigned to (a list of at most one); a department with none is refused.</summary>
    private async Task<List<Team>> AssignedTeamAsync(Guid departmentId, CancellationToken ct)
    {
        var department = await context.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct)
            ?? throw new KeyNotFoundException("Department not found.");
        if (!department.IsActive) throw new InvalidOperationException($"'{department.Name}' is inactive.");

        var team = await context.Teams.AsNoTracking().Include(t => t.Owner)
            .Where(t => t.DepartmentLinks.Any(d => d.DepartmentId == departmentId)).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(DepartmentTeams.NoTeamMessage(department.Name));
        if (!team.IsActive) throw new InvalidOperationException(DepartmentTeams.InactiveTeamMessage(department.Name, team.Name));
        return new List<Team> { team };
    }

    public async Task<BulkResultDto> ReassignAsync(BulkReassignRequestDto request, Guid actorId, CancellationToken ct = default)
    {
        var ids = Ids(request.CaseIds);

        // The target is checked once, before any case changes: a department, and an assignee who leads or belongs to its team.
        var assignees = await AssigneesAsync(request.DepartmentId, ct);
        var choices = assignees.Where(a => a.UserId == request.AssigneeUserId).ToList();
        if (choices.Count == 0)
            throw new ArgumentException("The chosen person cannot receive this department's cases. Choose the Team Lead or an agent of the team it is assigned to.");
        var choice = request.TeamId.HasValue
            ? choices.FirstOrDefault(a => a.TeamId == request.TeamId.Value) ?? throw new ArgumentException("The chosen person is not in that team.")
            : choices[0];
        var departmentName = await context.Departments.AsNoTracking().Where(d => d.Id == request.DepartmentId).Select(d => d.Name).FirstAsync(ct);
        var reason = await CaseReasons.ResolveAsync(context, CaseReasons.Assignment, request.Reason, request.ReasonDetail, ct);
        var actorName = await context.Users.AsNoTracking().Where(u => u.Id == actorId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "A supervisor";

        var result = new BulkResultDto { Requested = ids.Count };
        foreach (var caseId in ids)
        {
            var item = new BulkResultItemDto { CaseId = caseId };
            try
            {
                if (!await access.CanSeeAsync(caseId, ct)) throw new KeyNotFoundException("Case not found.");
                await ReassignOneAsync(caseId, choice, request.DepartmentId, departmentName, reason.Text, actorId, actorName, item, ct);
                item.Succeeded = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.Error = ex is AssignmentNotAllowedException or InvalidOperationException or ArgumentException or KeyNotFoundException or CaseLockedException
                    ? ex.Message : "The case could not be reassigned.";
                if (item.Error == "The case could not be reassigned.") logger.LogError(ex, "Bulk reassign failed for case {CaseId}.", caseId);
            }
            finally
            {
                context.ChangeTracker.Clear();
            }
            result.Items.Add(item);
        }
        Count(result);

        if (result.Succeeded > 0 && choice.UserId != actorId)
        {
            try
            {
                // One notification naming the case, or one for the whole batch with the count and the case numbers.
                await notifications.CreateBulkReassignedNotificationAsync(choice.UserId,
                    result.Items.Where(i => i.Succeeded).Select(i => (i.CaseId, i.CaseNumber ?? string.Empty)).ToList(), ct);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Bulk reassign notification failed."); }
        }
        return result;
    }

    private async Task ReassignOneAsync(Guid caseId, BulkAssigneeDto choice, Guid departmentId, string departmentName, string reason,
        Guid actorId, string actorName, BulkResultItemDto item, CancellationToken ct)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            var c = await context.Cases.Include(x => x.Owner).Include(x => x.Team).Include(x => x.Department)
                .FirstOrDefaultAsync(x => x.Id == caseId, ct) ?? throw new KeyNotFoundException("Case not found.");
            item.CaseNumber = c.CaseNumber;
            if (CaseWorkflow.IsFinal(c.Status))
                throw new InvalidOperationException($"The case is {CaseWorkflow.Label(c.Status)} and cannot be reassigned.");
            if (c.OwnerId == choice.UserId && c.TeamId == choice.TeamId && c.DepartmentId == departmentId)
                throw new InvalidOperationException($"{choice.Name} already holds this case in {choice.TeamName}.");

            // The same eligibility as Assign (capacity, manual-assignment setting), under the same per-team lock.
            await TeamAssignmentLock.AcquireAsync(context, choice.TeamId, ct);
            var policy = await eligibility.GetManualPolicyAsync(ct);
            var (team, verdict) = await eligibility.CheckAsync(choice.TeamId, choice.UserId, policy, ct);
            if (!verdict.IsEligible) throw new AssignmentNotAllowedException(verdict.Reason.ToString(), verdict.Explain(team.Team.Name));

            var before = $"{c.Owner.Name} · {c.Team.Name} · {c.Department?.Name ?? "no department"}";
            c.OwnerId = choice.UserId;
            c.TeamId = choice.TeamId;
            c.DepartmentId = departmentId;
            var now = DateTime.UtcNow;
            context.CaseEvents.Add(new CaseEvent
            {
                CaseId = c.Id,
                EventType = EventType.Assign,
                ActionType = "BULK_REASSIGN",
                OldValue = before,
                NewValue = $"{choice.Name} · {choice.TeamName} · {departmentName}",
                Message = $"[Bulk Reassign] Reassigned to {choice.Name} ({choice.Role}, {choice.TeamName}) in {departmentName} by {actorName}. Reason: {reason}.",
                CreatedAt = now,
                UserId = actorId
            });
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }

    public async Task<BulkResultDto> ResolveAsync(BulkResolveRequestDto request, Guid actorId, CancellationToken ct = default)
    {
        var ids = Ids(request.CaseIds);
        if (string.IsNullOrWhiteSpace(request.Disposition))
            throw new ArgumentException("Choose a disposition for the resolution.");

        var numbers = await context.Cases.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.CaseNumber, ct);
        var result = new BulkResultDto { Requested = ids.Count };
        foreach (var caseId in ids)
        {
            var item = new BulkResultItemDto { CaseId = caseId, CaseNumber = numbers.GetValueOrDefault(caseId) };
            try
            {
                if (!await access.CanSeeAsync(caseId, ct)) throw new KeyNotFoundException("Case not found.");
                await cases.ResolveCaseAsync(caseId, new ResolveCaseDto
                {
                    Disposition = request.Disposition, ResolutionNote = request.ResolutionNote, UserId = actorId,
                }, bulk: true);
                item.Succeeded = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.Error = ex is InvalidOperationException or ArgumentException or KeyNotFoundException or CaseLockedException
                    ? ex.Message : "The case could not be resolved.";
                if (item.Error == "The case could not be resolved.") logger.LogError(ex, "Bulk resolve failed for case {CaseId}.", caseId);
            }
            finally
            {
                context.ChangeTracker.Clear();
            }
            result.Items.Add(item);
        }
        Count(result);
        return result;
    }

    private static List<Guid> Ids(IEnumerable<Guid>? caseIds)
    {
        var ids = (caseIds ?? Enumerable.Empty<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) throw new ArgumentException("Select at least one case.");
        if (ids.Count > MaxCasesPerRequest) throw new ArgumentException($"Select at most {MaxCasesPerRequest} cases at a time.");
        return ids;
    }

    private static void Count(BulkResultDto result)
    {
        result.Succeeded = result.Items.Count(i => i.Succeeded);
        result.Failed = result.Items.Count - result.Succeeded;
    }
}
