namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

/// <summary>
/// Teams are Departments. A team's people are its ACTIVE TeamMembers — the one and only membership source, which routing,
/// the team board and monitoring all use. A team may carry its own assignment settings; otherwise it follows the global ones.
/// </summary>
public class TeamService : ITeamService
{
    private readonly AppDbContext _context;
    private readonly IRoutingEngineService _routing;

    public TeamService(AppDbContext context, IRoutingEngineService routing)
    {
        _context = context;
        _routing = routing;
    }

    // ------------------------------------------------------------------------------------------------ reads

    public async Task<IEnumerable<TeamDto>> GetTeamsAsync(CancellationToken ct = default) => await LoadAsync(null, ct);

    public async Task<TeamDto?> GetTeamByIdAsync(Guid id, CancellationToken ct = default) => (await LoadAsync(id, ct)).FirstOrDefault();

    /// <summary>All teams (or one) in a fixed number of queries; case counts are computed in the database, not by loading cases.</summary>
    private async Task<List<TeamDto>> LoadAsync(Guid? onlyId, CancellationToken ct)
    {
        var departments = await _context.Departments.AsNoTracking().Include(d => d.Owner)
            .Where(d => onlyId == null || d.Id == onlyId).OrderBy(d => d.Name).ToListAsync(ct);
        var ids = departments.Select(d => d.Id).ToList();

        var members = await _context.TeamMembers.AsNoTracking().Include(m => m.User)
            .Where(m => ids.Contains(m.DepartmentId) && m.IsActive).ToListAsync(ct);

        var openCounts = await _context.Cases.AsNoTracking()
            .Where(c => ids.Contains(c.DepartmentId) && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled)
            .GroupBy(c => c.DepartmentId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var globalConfig = await _routing.GetAssignmentConfigAsync(null, ct);
        var ownConfigs = await _context.AssignmentConfigurations.AsNoTracking()
            .Where(c => c.DepartmentId != null && ids.Contains(c.DepartmentId!.Value) && c.IsActive).ToDictionaryAsync(c => c.DepartmentId!.Value, ct);

        var result = new List<TeamDto>();
        foreach (var d in departments)
        {
            var teamMembers = members.Where(m => m.DepartmentId == d.Id && m.User != null)
                .Select(m => new TeamMemberDto
                {
                    Id = m.Id,
                    UserId = m.UserId,
                    Name = m.User.Name,
                    Email = m.User.Email,
                    Role = !string.IsNullOrWhiteSpace(m.MemberRole) ? m.MemberRole : (string.IsNullOrWhiteSpace(m.User.Role) ? "Service Agent" : m.User.Role),
                    Status = m.User.Status.ToString(),
                    IsLead = d.OwnerId == m.UserId,
                    IsAssignable = m.IsAssignable,
                })
                .OrderByDescending(m => m.IsLead).ThenBy(m => m.Name).ToList();

            ownConfigs.TryGetValue(d.Id, out var own);
            result.Add(new TeamDto
            {
                Id = d.Id,
                Name = d.Name,
                Code = d.Code,
                Function = d.Function,
                TeamLeadId = d.OwnerId,
                TeamLeadName = d.Owner?.Name ?? string.Empty,
                TeamLeadEmail = d.Owner?.Email ?? string.Empty,
                MemberCount = teamMembers.Count,
                QueueCount = openCounts.GetValueOrDefault(d.Id),
                IsActive = d.IsActive,
                CreatedAt = d.CreatedAt,
                AssignmentAlgorithm = own?.Algorithm ?? globalConfig.Algorithm,
                MaxConcurrentCapacity = own?.MaxConcurrentCapacity ?? globalConfig.MaxConcurrentCapacity,
                HasOwnAssignmentSettings = own != null,
                Members = teamMembers,
            });
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------------ writes

    public async Task<TeamDto> CreateTeamAsync(CreateTeamDto dto, Guid currentUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new InvalidOperationException("Team name is required.");

        var name = dto.Name.Trim();
        var code = !string.IsNullOrWhiteSpace(dto.Code) ? dto.Code.Trim().ToUpperInvariant() : GenerateTeamCode(name);

        if (await _context.Departments.AnyAsync(d => d.Name.ToLower() == name.ToLower() || d.Code.ToLower() == code.ToLower(), ct))
            throw new InvalidOperationException($"A team or department with name '{name}' or code '{code}' already exists.");

        await EnsureUserIsActiveAsync(dto.TeamLeadId, "team lead", ct);

        var department = new Department
        {
            Id = Guid.NewGuid(),
            Name = name,
            Code = code,
            Function = dto.Function?.Trim() ?? string.Empty,
            OwnerId = dto.TeamLeadId,
            IsActive = dto.IsActive,
            CreatedAt = DateTime.UtcNow
        };
        _context.Departments.Add(department);

        await SyncMembersAsync(department, new List<TeamMember>(), WantedMembers(dto.Members, dto.MemberUserIds), dto.TeamLeadId, ct);

        _context.CaseEvents.Add(Audit("CREATE", department.Name, $"Created team '{department.Name}'", null, JsonSerializer.Serialize(new { dto.Name, dto.Code, dto.TeamLeadId }), currentUserId));
        await _context.SaveChangesAsync(ct);

        await ApplyAssignmentSettingsAsync(department.Id, dto.AssignmentAlgorithm, dto.MaxConcurrentCapacity, false, currentUserId, ct);
        return (await GetTeamByIdAsync(department.Id, ct))!;
    }

    public async Task<TeamDto> UpdateTeamAsync(Guid id, UpdateTeamDto dto, Guid currentUserId, CancellationToken ct = default)
    {
        var department = await _context.Departments.Include(d => d.Members).FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new KeyNotFoundException($"Team with ID '{id}' was not found.");

        var oldState = JsonSerializer.Serialize(new { department.Name, department.Function, department.OwnerId, department.IsActive });

        if (!string.IsNullOrWhiteSpace(dto.Name) && !string.Equals(dto.Name.Trim(), department.Name, StringComparison.Ordinal))
        {
            var name = dto.Name.Trim();
            if (await _context.Departments.AnyAsync(d => d.Id != id && d.Name.ToLower() == name.ToLower(), ct))
                throw new InvalidOperationException($"A team named '{name}' already exists.");
            department.Name = name;
        }
        if (dto.Function != null) department.Function = dto.Function.Trim();
        if (dto.IsActive.HasValue) department.IsActive = dto.IsActive.Value;
        if (dto.TeamLeadId.HasValue)
        {
            await EnsureUserIsActiveAsync(dto.TeamLeadId, "team lead", ct);
            department.OwnerId = dto.TeamLeadId.Value;
        }
        department.UpdatedAt = DateTime.UtcNow;

        var wanted = WantedMembers(dto.Members, dto.MemberUserIds);
        if (wanted != null || dto.TeamLeadId.HasValue)
        {
            var current = department.Members.ToList();
            // Changing only the lead must not touch who is on the team: keep the current members and make sure the lead is one.
            wanted ??= current.Where(m => m.IsActive).Select(m => (m.UserId, m.IsAssignable)).ToList();
            await SyncMembersAsync(department, current, wanted, department.OwnerId, ct);
        }

        _context.CaseEvents.Add(Audit("UPDATE", department.Name, $"Updated team '{department.Name}'", oldState,
            JsonSerializer.Serialize(new { department.Name, department.Function, department.OwnerId, department.IsActive }), currentUserId));
        await _context.SaveChangesAsync(ct);

        await ApplyAssignmentSettingsAsync(department.Id, dto.AssignmentAlgorithm, dto.MaxConcurrentCapacity, dto.UseGlobalAssignmentSettings, currentUserId, ct);
        return (await GetTeamByIdAsync(department.Id, ct))!;
    }

    private static List<(Guid UserId, bool IsAssignable)>? WantedMembers(List<TeamMemberInputDto>? members, List<Guid>? ids) =>
        members != null ? members.GroupBy(m => m.UserId).Select(g => (g.Key, g.Last().IsAssignable)).ToList()
        : ids != null ? ids.Distinct().Select(i => (i, true)).ToList()
        : null;

    /// <summary>Makes the team's members exactly <paramref name="wanted"/> (plus the lead). Existing rows are reused so history is kept.</summary>
    private async Task SyncMembersAsync(Department department, List<TeamMember> current, List<(Guid UserId, bool IsAssignable)>? wanted, Guid? leadId, CancellationToken ct)
    {
        wanted ??= new List<(Guid, bool)>();
        // A lead is on their team's board, but does not receive cases automatically unless they were chosen as a member too:
        // the lead is who holds a case when no agent can take it.
        if (leadId.HasValue && wanted.All(w => w.UserId != leadId.Value)) wanted.Add((leadId.Value, false));

        var wantedIds = wanted.Select(w => w.UserId).ToHashSet();
        var users = await _context.Users.Where(u => wantedIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var missing = wantedIds.Where(i => !users.ContainsKey(i)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("One or more selected members no longer exist. Reload and try again.");

        foreach (var row in current.Where(m => !wantedIds.Contains(m.UserId)))
            _context.TeamMembers.Remove(row);

        foreach (var (userId, assignable) in wanted)
        {
            var user = users[userId];
            var row = current.FirstOrDefault(m => m.UserId == userId);
            if (row == null)
            {
                _context.TeamMembers.Add(new TeamMember
                {
                    Id = Guid.NewGuid(), DepartmentId = department.Id, UserId = userId,
                    MemberRole = userId == leadId ? "Team Lead" : (string.IsNullOrWhiteSpace(user.Role) ? "Service Agent" : user.Role),
                    IsAssignable = assignable, IsActive = true, JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
                });
            }
            else
            {
                row.IsActive = true;
                row.IsAssignable = assignable;
            }
        }
    }

    private async Task ApplyAssignmentSettingsAsync(Guid teamId, string? algorithm, int? capacity, bool useGlobal, Guid actingUserId, CancellationToken ct)
    {
        if (useGlobal)
        {
            await _routing.ClearTeamAssignmentConfigAsync(teamId, actingUserId, ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(algorithm) && !capacity.HasValue) return;

        var effective = await _routing.GetAssignmentConfigAsync(teamId, ct);
        await _routing.UpdateAssignmentConfigAsync(
            new UpdateAssignmentConfigDto { Algorithm = string.IsNullOrWhiteSpace(algorithm) ? effective.Algorithm : algorithm, MaxConcurrentCapacity = capacity ?? effective.MaxConcurrentCapacity },
            actingUserId, teamId, ct);
    }

    private async Task EnsureUserIsActiveAsync(Guid? userId, string role, CancellationToken ct)
    {
        if (!userId.HasValue) return;
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct)
            ?? throw new InvalidOperationException($"The chosen {role} does not exist.");
        if (!user.IsActive) throw new InvalidOperationException($"{user.Name} is deactivated and cannot be the {role}.");
    }

    public async Task DeleteTeamAsync(Guid id, Guid currentUserId, CancellationToken ct = default)
    {
        var department = await _context.Departments.Include(d => d.Members).FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new KeyNotFoundException($"Team with ID '{id}' was not found.");

        var active = await _context.Cases.CountAsync(c => c.DepartmentId == id && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled, ct);
        if (active > 0)
            throw new InvalidOperationException($"Cannot delete team '{department.Name}' because it currently has {active} active cases in queue. Please reassign or close cases before deleting.");

        var rules = await _context.RoutingRules.Where(r => r.TargetDepartmentId == id).Select(r => r.Name).ToListAsync(ct);
        if (rules.Count > 0)
            throw new InvalidOperationException($"Cannot delete team '{department.Name}' because routing rules send cases to it: {string.Join(", ", rules)}. Change or remove those rules first.");

        if (await _context.Cases.AnyAsync(c => c.DepartmentId == id, ct))
            throw new InvalidOperationException($"Team '{department.Name}' has case history, so it cannot be deleted. Deactivate it instead.");

        _context.TeamMembers.RemoveRange(department.Members);
        _context.Departments.Remove(department);
        _context.CaseEvents.Add(Audit("DELETE", department.Name, $"Deleted team '{department.Name}'", department.Name, null, currentUserId));
        await _context.SaveChangesAsync(ct);
    }

    public async Task AddMemberAsync(Guid teamId, AddTeamMemberDto dto, Guid currentUserId, CancellationToken ct = default)
    {
        var user = await _context.Users.FindAsync(new object[] { dto.UserId }, ct) ?? throw new KeyNotFoundException("User not found.");
        if (!user.IsActive) throw new InvalidOperationException($"{user.Name} is deactivated and cannot join a team.");
        if (!await _context.Departments.AnyAsync(d => d.Id == teamId, ct)) throw new KeyNotFoundException("Team not found.");

        var row = await _context.TeamMembers.FirstOrDefaultAsync(tm => tm.DepartmentId == teamId && tm.UserId == dto.UserId, ct);
        if (row == null)
        {
            _context.TeamMembers.Add(new TeamMember
            {
                Id = Guid.NewGuid(), DepartmentId = teamId, UserId = dto.UserId,
                MemberRole = !string.IsNullOrWhiteSpace(dto.MemberRole) ? dto.MemberRole : (string.IsNullOrWhiteSpace(user.Role) ? "Service Agent" : user.Role),
                IsAssignable = dto.IsAssignable, IsActive = true, JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
            });
        }
        else
        {
            row.IsActive = true;
            row.IsAssignable = dto.IsAssignable;
        }

        _context.CaseEvents.Add(Audit("ADD_MEMBER", user.Name, $"Added member '{user.Name}' to team", null, dto.MemberRole, currentUserId));
        await _context.SaveChangesAsync(ct);
    }

    public async Task RemoveMemberAsync(Guid teamId, Guid userId, Guid currentUserId, CancellationToken ct = default)
    {
        var member = await _context.TeamMembers.Include(m => m.User).FirstOrDefaultAsync(tm => tm.DepartmentId == teamId && tm.UserId == userId, ct);
        if (member == null) return;

        _context.TeamMembers.Remove(member);
        _context.CaseEvents.Add(Audit("REMOVE_MEMBER", member.User?.Name ?? userId.ToString(), "Removed member from team", null, null, currentUserId));
        await _context.SaveChangesAsync(ct);
    }

    public async Task<TeamDto> ToggleTeamStatusAsync(Guid id, Guid currentUserId, CancellationToken ct = default)
    {
        var department = await _context.Departments.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new KeyNotFoundException($"Team with ID '{id}' was not found.");

        department.IsActive = !department.IsActive;
        department.UpdatedAt = DateTime.UtcNow;
        _context.CaseEvents.Add(Audit("TOGGLE_STATUS", department.Name, $"Toggled team '{department.Name}' status to {(department.IsActive ? "ACTIVE" : "INACTIVE")}", (!department.IsActive).ToString(), department.IsActive.ToString(), currentUserId));
        await _context.SaveChangesAsync(ct);

        return (await GetTeamByIdAsync(department.Id, ct))!;
    }

    private static string GenerateTeamCode(string name)
    {
        var words = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1)
            return words[0].Length >= 3 ? words[0][..3].ToUpperInvariant() : words[0].ToUpperInvariant() + "T";
        var initials = string.Concat(words.Select(w => w[0])).ToUpperInvariant();
        return initials.Length > 5 ? initials[..5] : initials;
    }

    private static CaseEvent Audit(string actionType, string entityName, string description, string? oldValue, string? newValue, Guid userId) => new()
    {
        Id = Guid.NewGuid(), CaseId = null, EventType = EventType.Other, Message = description, CreatedAt = DateTime.UtcNow,
        UserId = userId, Module = "Teams", EntityName = entityName, ActionType = actionType, OldValue = oldValue, NewValue = newValue
    };
}
