namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

/// <summary>
/// Teams are Departments. A team's people are its ACTIVE TeamMembers — the one and only membership source, which routing,
/// the team board and monitoring all use. A team may carry its own assignment settings; otherwise it follows the global ones.
/// A team handles any number of departments (<see cref="Department"/>), each of which belongs to at most one team. Every save is held to the Create/Edit Team form
/// as configured (FieldConfiguration, section <see cref="TeamFormFields.SectionKey"/>: required fields, formats, custom fields)
/// and to the team rules (<see cref="ITeamSettingsService"/>) — the same checks whatever screen or client sent it.
/// </summary>
public class TeamService : ITeamService
{
    private readonly AppDbContext _context;
    private readonly IRoutingEngineService _routing;
    private readonly ITeamSettingsService _settings;
    private readonly IDbContextFactory<AppDbContext>? _contexts;
    private readonly IFieldValidationEngine _fields;

    public TeamService(AppDbContext context, IRoutingEngineService routing, ITeamSettingsService? settings = null, IDbContextFactory<AppDbContext>? contexts = null,
        IFieldValidationEngine? fields = null)
    {
        _context = context;
        _routing = routing;
        _settings = settings ?? new TeamSettingsService(context);
        _contexts = contexts;
        _fields = fields ?? new FieldValidationEngine(context, new ConfigCache(new MemoryCache(new MemoryCacheOptions())), NullLogger<FieldValidationEngine>.Instance);
    }

    // ------------------------------------------------------------------------------------------------ reads

    public async Task<IEnumerable<TeamDto>> GetTeamsAsync(CancellationToken ct = default) => await LoadAsync(null, ct);

    public async Task<TeamDto?> GetTeamByIdAsync(Guid id, CancellationToken ct = default) => (await LoadAsync(id, ct)).FirstOrDefault();

    private sealed record MemberRow(Guid Id, Guid DepartmentId, Guid UserId, string? ExternalUserId, string Name, string Email, string? MemberRole, string? UserRole, UserStatus Status, bool IsAssignable);

    /// <summary>
    /// All teams (or one) in a fixed number of queries, run side by side; case counts are computed in the database, not by
    /// loading cases.
    /// </summary>
    private async Task<List<TeamDto>> LoadAsync(Guid? onlyId, CancellationToken ct)
    {
        List<Team> departments = new();
        List<MemberRow> members = new();
        Dictionary<Guid, int> openCounts = new();
        Dictionary<Guid, AssignmentConfiguration> ownConfigs = new();
        List<(Guid TeamId, TeamDepartmentDto Department)> links = new();
        List<TeamCustomAttribute> custom = new();
        AssignmentConfigDto globalConfig = null!;

        await ParallelReads.RunAsync(_contexts, _context, ct,
            async db => departments = await db.Teams.AsNoTracking().Include(d => d.Owner)
                .Where(d => onlyId == null || d.Id == onlyId).OrderBy(d => d.Name).ToListAsync(ct),
            async db => members = await db.TeamMembers.AsNoTracking()
                .Where(m => (onlyId == null || m.TeamId == onlyId) && m.IsActive)
                .Select(m => new MemberRow(m.Id, m.TeamId, m.UserId, m.User.ExternalUserId, m.User.Name, m.User.Email, m.MemberRole, m.User.Role, m.User.Status, m.IsAssignable))
                .ToListAsync(ct),
            async db => openCounts = await db.Cases.AsNoTracking()
                .Where(c => (onlyId == null || c.TeamId == onlyId) && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled)
                .GroupBy(c => c.TeamId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct),
            async db => ownConfigs = await db.AssignmentConfigurations.AsNoTracking()
                .Where(c => c.TeamId != null && (onlyId == null || c.TeamId == onlyId) && c.IsActive).ToDictionaryAsync(c => c.TeamId!.Value, ct),
            async db => links = (await db.TeamDepartments.AsNoTracking()
                    .Where(l => onlyId == null || l.TeamId == onlyId)
                    .OrderBy(l => l.Department.DisplayOrder).ThenBy(l => l.Department.Name)
                    .Select(l => new { l.TeamId, l.Department.Id, l.Department.Name, l.Department.Code, l.Department.IsActive })
                    .ToListAsync(ct))
                .Select(l => (l.TeamId, new TeamDepartmentDto { Id = l.Id, Name = l.Name, Code = l.Code, IsActive = l.IsActive })).ToList(),
            async db => custom = await db.TeamCustomAttributes.AsNoTracking().Where(a => onlyId == null || a.TeamId == onlyId).ToListAsync(ct));

        // The routing engine reads through the request's context (and its configuration cache), so it runs on its own.
        globalConfig = await _routing.GetAssignmentConfigAsync(null, ct);

        var result = new List<TeamDto>();
        foreach (var d in departments)
        {
            var teamMembers = members.Where(m => m.DepartmentId == d.Id)
                .Select(m => new TeamMemberDto
                {
                    Id = m.Id,
                    UserId = m.UserId,
                    ExternalUserId = m.ExternalUserId,
                    Name = m.Name,
                    Email = m.Email,
                    Role = !string.IsNullOrWhiteSpace(m.MemberRole) ? m.MemberRole : (string.IsNullOrWhiteSpace(m.UserRole) ? "Service Agent" : m.UserRole),
                    Status = m.Status.ToString(),
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
                Departments = links.Where(l => l.TeamId == d.Id).Select(l => l.Department).ToList(),
                CustomAttributes = custom.Where(a => a.TeamId == d.Id).ToDictionary(a => a.FieldKey, a => a.FieldValue, StringComparer.OrdinalIgnoreCase),
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

        if (await _context.Teams.AnyAsync(d => d.Name.ToLower() == name.ToLower() || d.Code.ToLower() == code.ToLower(), ct))
            throw new InvalidOperationException($"A team or department with name '{name}' or code '{code}' already exists.");

        await EnsureUserIsActiveAsync(dto.TeamLeadId, "team lead", ct);

        var policy = await _settings.GetPolicyAsync(ct);
        var wanted = WantedMembers(dto.Members, dto.MemberUserIds);
        var departmentIds = (dto.DepartmentIds ?? new List<Guid>()).Distinct().ToList();
        var form = await CheckFormAsync(policy, name, dto.Code, dto.Function, departmentIds, dto.TeamLeadId, wanted ?? new(), dto.IsActive,
            dto.CustomAttributes, new Dictionary<string, string>(), ct);

        var department = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Code = code,
            Function = dto.Function?.Trim() ?? string.Empty,
            OwnerId = dto.TeamLeadId,
            IsActive = dto.IsActive,
            CreatedAt = DateTime.UtcNow
        };

        if (!dto.IsActive && departmentIds.Count > 0)
            throw new FieldValidationException(new[] { new FieldError(TeamFormFields.Departments,
                "An inactive team cannot be assigned departments: its departments could not take new cases. Create the team as active, or leave its departments empty.") });

        await RunGuardedAsync(async () =>
        {
            await CheckDepartmentsAsync(departmentIds, new HashSet<Guid>(), ct);
            await DepartmentTeams.EnsureNotMappedElsewhereAsync(_context, departmentIds, department.Id, TeamFormFields.Departments, ct);
            await CheckPoliciesAsync(policy, department.Id, dto.TeamLeadId, wanted, ct);

            _context.Teams.Add(department);
            await SyncMembersAsync(department, new List<TeamMember>(), wanted, dto.TeamLeadId, ct);
            foreach (var id in departmentIds)
                _context.TeamDepartments.Add(new TeamDepartment { TeamId = department.Id, DepartmentId = id, LinkedAt = DateTime.UtcNow });
            await SyncCustomAttributesAsync(department.Id, form, ct);

            _context.CaseEvents.Add(Audit("CREATE", department.Name, $"Created team '{department.Name}'", null,
                JsonSerializer.Serialize(new { dto.Name, dto.Code, dto.TeamLeadId, DepartmentIds = departmentIds }), currentUserId));
            await _context.SaveChangesAsync(ct);
        }, ct);

        await ApplyAssignmentSettingsAsync(department.Id, dto.AssignmentAlgorithm, dto.MaxConcurrentCapacity, false, currentUserId, ct);
        return (await GetTeamByIdAsync(department.Id, ct))!;
    }

    public async Task<TeamDto> UpdateTeamAsync(Guid id, UpdateTeamDto dto, Guid currentUserId, CancellationToken ct = default)
    {
        var department = await _context.Teams.Include(d => d.Members).Include(d => d.DepartmentLinks).FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new KeyNotFoundException($"Team with ID '{id}' was not found.");

        var oldState = JsonSerializer.Serialize(new { department.Name, department.Function, department.OwnerId, department.IsActive, DepartmentIds = department.DepartmentLinks.Select(l => l.DepartmentId) });
        var policy = await _settings.GetPolicyAsync(ct);

        if (!string.IsNullOrWhiteSpace(dto.Name) && !string.Equals(dto.Name.Trim(), department.Name, StringComparison.Ordinal))
        {
            var name = dto.Name.Trim();
            if (await _context.Teams.AnyAsync(d => d.Id != id && d.Name.ToLower() == name.ToLower(), ct))
                throw new InvalidOperationException($"A team named '{name}' already exists.");
            department.Name = name;
        }
        if (dto.TeamLeadId.HasValue) await EnsureUserIsActiveAsync(dto.TeamLeadId, "team lead", ct);

        // What the team will look like after this save; a field the form did not send keeps its current value.
        var current = department.Members.ToList();
        var leadAfter = dto.TeamLeadId ?? department.OwnerId;
        var wanted = WantedMembers(dto.Members, dto.MemberUserIds);
        var membersAfter = wanted ?? current.Where(m => m.IsActive).Select(m => (m.UserId, m.IsAssignable)).ToList();
        var linkedBefore = department.DepartmentLinks.Select(l => l.DepartmentId).ToHashSet();
        var activeAfter = dto.IsActive ?? department.IsActive;
        // A team being deactivated releases its departments (they become unassigned, never moved to another team): an
        // inactive team cannot take cases, so a department left on it could not be used. The release is audited below.
        var deactivating = department.IsActive && !activeAfter;
        if (!activeAfter && !deactivating && dto.DepartmentIds is { Count: > 0 })
            throw new FieldValidationException(new[] { new FieldError(TeamFormFields.Departments,
                "An inactive team cannot be assigned departments. Activate the team first.") });
        var departmentsAfter = deactivating ? new List<Guid>() : dto.DepartmentIds?.Distinct().ToList() ?? linkedBefore.ToList();
        var departmentsChange = deactivating ? linkedBefore.Count > 0 : dto.DepartmentIds != null;
        var customBefore = await _context.TeamCustomAttributes.AsNoTracking().Where(a => a.TeamId == id).ToDictionaryAsync(a => a.FieldKey, a => a.FieldValue, StringComparer.OrdinalIgnoreCase, ct);
        var form = await CheckFormAsync(policy, department.Name, dto.Code ?? department.Code, dto.Function ?? department.Function, departmentsAfter, leadAfter, membersAfter,
            activeAfter, dto.CustomAttributes, customBefore, ct);

        if (department.IsActive && !activeAfter) await EnsureCanDeactivateAsync(policy, department, ct);

        await RunGuardedAsync(async () =>
        {
            if (departmentsChange)
            {
                await CheckDepartmentsAsync(departmentsAfter, linkedBefore, ct);
                await DepartmentTeams.EnsureNotMappedElsewhereAsync(_context, departmentsAfter, department.Id, TeamFormFields.Departments, ct);
            }
            await CheckPoliciesAsync(policy, department.Id, leadAfter, membersAfter, ct);

            if (dto.Function != null) department.Function = dto.Function.Trim();
            if (dto.IsActive.HasValue) department.IsActive = dto.IsActive.Value;
            if (dto.TeamLeadId.HasValue) department.OwnerId = dto.TeamLeadId.Value;
            department.UpdatedAt = DateTime.UtcNow;

            if (wanted != null || dto.TeamLeadId.HasValue)
            {
                // Changing only the lead must not touch who is on the team: keep the current members and make sure the lead is one.
                var keep = wanted ?? current.Where(m => m.IsActive).Select(m => (m.UserId, m.IsAssignable)).ToList();
                await SyncMembersAsync(department, current, keep, department.OwnerId, ct);
            }

            if (departmentsChange)
            {
                var after = departmentsAfter.ToHashSet();
                if (deactivating && linkedBefore.Count > 0)
                {
                    var released = await _context.Departments.AsNoTracking().Where(d => linkedBefore.Contains(d.Id)).OrderBy(d => d.Name).Select(d => d.Name).ToListAsync(ct);
                    _context.CaseEvents.Add(Audit("UPDATE", department.Name,
                        $"Deactivated team '{department.Name}': released {string.Join(", ", released)} (now unassigned)", string.Join(", ", released), "Unassigned", currentUserId));
                }
                _context.TeamDepartments.RemoveRange(department.DepartmentLinks.Where(l => !after.Contains(l.DepartmentId)));
                foreach (var add in after.Where(a => !linkedBefore.Contains(a)))
                    _context.TeamDepartments.Add(new TeamDepartment { TeamId = department.Id, DepartmentId = add, LinkedAt = DateTime.UtcNow });
            }
            if (dto.CustomAttributes != null) await SyncCustomAttributesAsync(department.Id, form, ct);

            _context.CaseEvents.Add(Audit("UPDATE", department.Name, $"Updated team '{department.Name}'", oldState,
                JsonSerializer.Serialize(new { department.Name, department.Function, department.OwnerId, department.IsActive, DepartmentIds = departmentsAfter }), currentUserId));
            await _context.SaveChangesAsync(ct);
        }, ct);

        await ApplyAssignmentSettingsAsync(department.Id, dto.AssignmentAlgorithm, dto.MaxConcurrentCapacity, dto.UseGlobalAssignmentSettings, currentUserId, ct);
        return (await GetTeamByIdAsync(department.Id, ct))!;
    }

    /// <summary>Runs a team save under the team write lock; a department claimed by a concurrent save becomes a field error, not a 500.</summary>
    private async Task RunGuardedAsync(Func<Task> work, CancellationToken ct)
    {
        try { await TeamWriteLock.RunAsync(_context, work, ct); }
        catch (DbUpdateException ex) when (DepartmentTeams.IsDuplicateMapping(ex))
        {
            throw new FieldValidationException(new[] { new FieldError(TeamFormFields.Departments, DepartmentTeams.RaceMessage) });
        }
    }

    private static List<(Guid UserId, bool IsAssignable)>? WantedMembers(List<TeamMemberInputDto>? members, List<Guid>? ids) =>
        members != null ? members.GroupBy(m => m.UserId).Select(g => (g.Key, g.Last().IsAssignable)).ToList()
        : ids != null ? ids.Distinct().Select(i => (i, true)).ToList()
        : null;

    // ------------------------------------------------------------------------------------------------ Teams settings

    /// <summary>
    /// The Create/Edit Team form as configured (Case Settings → Teams → Create Team Form): every built-in field's required flag and
    /// format, and every custom field, checked by the same engine as Create Case. Also the save-time team rules that look only at
    /// this team (one department, members, a lead). Returns the custom field values to store, canonical; on update a custom field
    /// that was not sent keeps its stored value (and is validated as such).
    /// </summary>
    private async Task<Dictionary<string, string>> CheckFormAsync(TeamSettings policy, string name, string? code, string? description,
        IReadOnlyCollection<Guid> departmentIds, Guid? leadId, IReadOnlyCollection<(Guid UserId, bool IsAssignable)> members, bool isActive,
        Dictionary<string, string>? customSent, IReadOnlyDictionary<string, string> customStored, CancellationToken ct)
    {
        var custom = new Dictionary<string, string>(customStored, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in customSent ?? new Dictionary<string, string>()) custom[key] = value ?? string.Empty;

        var memberIds = members.Select(m => m.UserId).Where(u => u != leadId).Distinct().ToList();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [TeamFormFields.Name] = name,
            [TeamFormFields.Code] = code,
            [TeamFormFields.Description] = description,
            [TeamFormFields.Departments] = MultiSelectValues.Format(departmentIds.Select(d => d.ToString())),
            [TeamFormFields.TeamLead] = leadId?.ToString(),
            [TeamFormFields.Members] = MultiSelectValues.Format(memberIds.Select(m => m.ToString())),
        };
        foreach (var (key, value) in custom) values[key] = value;

        var result = await _fields.ValidateAsync("CaseManagement", TeamFormFields.SectionKey, values,
            customFieldKeys: customSent?.Keys, ct: ct);
        var errors = result.Errors.ToList();

        if (!policy.AllowMultipleDepartments && departmentIds.Count > 1)
            errors.Add(new FieldError(TeamFormFields.Departments, "A team can serve only one department (Case Settings → Teams → Team Rules)."));
        if (!policy.AllowTeamsWithoutMembers && memberIds.Count == 0)
            errors.Add(new FieldError(TeamFormFields.Members, "Add at least one member: a team cannot be saved without members (Case Settings → Teams → Team Rules)."));
        if (policy.RequireTeamLead && isActive && leadId == null)
            errors.Add(new FieldError(TeamFormFields.TeamLead, "Choose a team lead: every active team needs one (Case Settings → Teams → Team Rules)."));

        if (errors.Count > 0) throw new FieldValidationException(errors.GroupBy(e => e.Field).Select(g => g.First()));

        return custom.Where(kv => result.Normalized.ContainsKey(kv.Key) || kv.Value.Length == 0)
            .ToDictionary(kv => kv.Key, kv => result.Normalized.TryGetValue(kv.Key, out var v) ? v : kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"A team with open cases can be deactivated" switched off: refuse while it holds any.</summary>
    private async Task EnsureCanDeactivateAsync(TeamSettings policy, Team team, CancellationToken ct)
    {
        if (policy.AllowDeactivateWithOpenCases) return;
        var open = await _context.Cases.CountAsync(c => c.TeamId == team.Id && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled, ct);
        if (open > 0)
            throw new FieldValidationException(new[] { new FieldError("isActive",
                $"'{team.Name}' still holds {open} open {(open == 1 ? "case" : "cases")}. Move or resolve {(open == 1 ? "it" : "them")} before deactivating the team (Case Settings → Teams → Team Rules).") });
    }

    /// <summary>The team's custom field values become exactly <paramref name="values"/>; a blank value removes the stored one.</summary>
    private async Task SyncCustomAttributesAsync(Guid teamId, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var stored = await _context.TeamCustomAttributes.Where(a => a.TeamId == teamId).ToListAsync(ct);
        foreach (var row in stored.Where(r => !values.TryGetValue(r.FieldKey, out var v) || string.IsNullOrWhiteSpace(v)))
            _context.TeamCustomAttributes.Remove(row);
        foreach (var (key, value) in values.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)))
        {
            var row = stored.FirstOrDefault(r => string.Equals(r.FieldKey, key, StringComparison.OrdinalIgnoreCase));
            if (row == null) _context.TeamCustomAttributes.Add(new TeamCustomAttribute { TeamId = teamId, FieldKey = key, FieldValue = value, UpdatedAt = DateTime.UtcNow });
            else if (row.FieldValue != value) { row.FieldValue = value; row.UpdatedAt = DateTime.UtcNow; }
        }
    }

    /// <summary>Every chosen department exists and is active — except one the team is already linked to, which may stay.</summary>
    private async Task CheckDepartmentsAsync(IReadOnlyCollection<Guid> ids, IReadOnlySet<Guid> alreadyLinked, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        var found = await _context.Departments.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.Name, d.IsActive }).ToListAsync(ct);
        if (found.Count != ids.Count)
            throw new FieldValidationException(new[] { new FieldError(TeamFormFields.Departments, "One or more chosen departments no longer exist. Reload and choose again.") });
        var inactive = found.Where(d => !d.IsActive && !alreadyLinked.Contains(d.Id)).Select(d => d.Name).ToList();
        if (inactive.Count > 0)
            throw new FieldValidationException(new[] { new FieldError(TeamFormFields.Departments, $"{string.Join(", ", inactive)} {(inactive.Count == 1 ? "is" : "are")} inactive and cannot be added to a team.") });
    }

    /// <summary>
    /// "Several teams per person" and "several teams per lead". A person's member row in the team they lead does not count as
    /// a membership (the lead always has one). Runs inside <see cref="TeamWriteLock"/>, so two saves cannot both pass.
    /// </summary>
    private async Task CheckPoliciesAsync(TeamSettings policy, Guid teamId, Guid? leadId, IReadOnlyCollection<(Guid UserId, bool IsAssignable)>? members, CancellationToken ct)
    {
        var errors = new List<FieldError>();

        if (!policy.AllowMultipleTeamLeads && leadId.HasValue)
        {
            var otherTeam = await _context.Teams.AsNoTracking().Where(d => d.Id != teamId && d.OwnerId == leadId).Select(d => d.Name).FirstOrDefaultAsync(ct);
            if (otherTeam != null)
            {
                var name = await _context.Users.AsNoTracking().Where(u => u.Id == leadId).Select(u => u.Name).FirstOrDefaultAsync(ct);
                errors.Add(new FieldError(TeamFormFields.TeamLead, $"{name} already leads {otherTeam}. A person can lead only one team (Case Settings → Teams → Team Rules)."));
            }
        }

        if (!policy.AllowLeadInOtherTeams)
        {
            // The lead may not be a member of another team, and no member of this team may lead another one.
            if (leadId.HasValue)
            {
                var memberOf = await _context.TeamMembers.AsNoTracking()
                    .Where(m => m.IsActive && m.UserId == leadId && m.TeamId != teamId && m.Team.OwnerId != m.UserId)
                    .Select(m => new { m.User.Name, Team = m.Team.Name }).FirstOrDefaultAsync(ct);
                if (memberOf != null)
                    errors.Add(new FieldError(TeamFormFields.TeamLead, $"{memberOf.Name} is a member of {memberOf.Team}. A team lead cannot also be a member of another team (Case Settings → Teams → Team Rules)."));
            }
            if (members != null)
            {
                var ids = members.Select(m => m.UserId).Where(u => u != leadId).Distinct().ToList();
                var leaders = await _context.Teams.AsNoTracking()
                    .Where(d => d.Id != teamId && d.OwnerId != null && ids.Contains(d.OwnerId.Value))
                    .Select(d => new { d.Owner!.Name, Team = d.Name }).OrderBy(x => x.Name).ToListAsync(ct);
                if (leaders.Count > 0)
                    errors.Add(new FieldError(TeamFormFields.Members,
                        $"{string.Join(", ", leaders.Take(5).Select(x => $"{x.Name} (leads {x.Team})"))}: a team lead cannot also be a member of another team (Case Settings → Teams → Team Rules)."));
            }
        }

        if (!policy.AllowMultipleTeamMembership && members != null)
        {
            var ids = members.Select(m => m.UserId).Where(u => u != leadId).Distinct().ToList();
            var elsewhere = await _context.TeamMembers.AsNoTracking()
                .Where(m => m.IsActive && m.TeamId != teamId && ids.Contains(m.UserId) && m.Team.OwnerId != m.UserId)
                .Select(m => new { m.User.Name, Team = m.Team.Name })
                .OrderBy(x => x.Name).ToListAsync(ct);
            if (elsewhere.Count > 0)
            {
                var shown = elsewhere.Take(5).Select(x => $"{x.Name} ({x.Team})");
                errors.Add(new FieldError(TeamFormFields.Members,
                    $"Already in another team: {string.Join(", ", shown)}{(elsewhere.Count > 5 ? $" and {elsewhere.Count - 5} more" : "")}. A person can belong to only one team (Case Settings → Teams → Team Rules)."));
            }
        }

        if (errors.Count > 0) throw new FieldValidationException(errors);
    }

    /// <summary>Makes the team's members exactly <paramref name="wanted"/> (plus the lead). Existing rows are reused so history is kept.</summary>
    private async Task SyncMembersAsync(Team department, List<TeamMember> current, List<(Guid UserId, bool IsAssignable)>? wanted, Guid? leadId, CancellationToken ct)
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
                    Id = Guid.NewGuid(), TeamId = department.Id, UserId = userId,
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
        var department = await _context.Teams.Include(d => d.Members).FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new KeyNotFoundException($"Team with ID '{id}' was not found.");

        var active = await _context.Cases.CountAsync(c => c.TeamId == id && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled, ct);
        if (active > 0)
            throw new InvalidOperationException($"Cannot delete team '{department.Name}' because it currently has {active} active cases in queue. Please reassign or close cases before deleting.");

        var rules = await _context.RoutingRules.Where(r => r.TargetTeamId == id).Select(r => r.Name).ToListAsync(ct);
        if (rules.Count > 0)
            throw new InvalidOperationException($"Cannot delete team '{department.Name}' because routing rules send cases to it: {string.Join(", ", rules)}. Change or remove those rules first.");

        if (await _context.Cases.AnyAsync(c => c.TeamId == id, ct))
            throw new InvalidOperationException($"Team '{department.Name}' has case history, so it cannot be deleted. Deactivate it instead.");

        _context.TeamMembers.RemoveRange(department.Members);
        _context.Teams.Remove(department);   // its department links go with it (cascade)
        _context.CaseEvents.Add(Audit("DELETE", department.Name, $"Deleted team '{department.Name}'", department.Name, null, currentUserId));
        await _context.SaveChangesAsync(ct);
    }

    public async Task AddMemberAsync(Guid teamId, AddTeamMemberDto dto, Guid currentUserId, CancellationToken ct = default)
    {
        var user = await _context.Users.FindAsync(new object[] { dto.UserId }, ct) ?? throw new KeyNotFoundException("User not found.");
        if (!user.IsActive) throw new InvalidOperationException($"{user.Name} is deactivated and cannot join a team.");
        var leadId = await _context.Teams.Where(d => d.Id == teamId).Select(d => new { d.OwnerId }).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Team not found.");
        var policy = await _settings.GetPolicyAsync(ct);

        await TeamWriteLock.RunAsync(_context, async () =>
        {
            await CheckPoliciesAsync(policy, teamId, leadId.OwnerId, new[] { (dto.UserId, dto.IsAssignable) }, ct);

            var row = await _context.TeamMembers.FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == dto.UserId, ct);
            if (row == null)
            {
                _context.TeamMembers.Add(new TeamMember
                {
                    Id = Guid.NewGuid(), TeamId = teamId, UserId = dto.UserId,
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
        }, ct);
    }

    public async Task RemoveMemberAsync(Guid teamId, Guid userId, Guid currentUserId, CancellationToken ct = default)
    {
        var member = await _context.TeamMembers.Include(m => m.User).FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == userId, ct);
        if (member == null) return;

        _context.TeamMembers.Remove(member);
        _context.CaseEvents.Add(Audit("REMOVE_MEMBER", member.User?.Name ?? userId.ToString(), "Removed member from team", null, null, currentUserId));
        await _context.SaveChangesAsync(ct);
    }

    public async Task<TeamDto> ToggleTeamStatusAsync(Guid id, Guid currentUserId, CancellationToken ct = default)
    {
        var department = await _context.Teams.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new KeyNotFoundException($"Team with ID '{id}' was not found.");

        // The switch is a save like any other: the lifecycle and leadership rules apply to it too.
        var policy = await _settings.GetPolicyAsync(ct);
        if (department.IsActive) await EnsureCanDeactivateAsync(policy, department, ct);
        else if (policy.RequireTeamLead && department.OwnerId == null)
            throw new FieldValidationException(new[] { new FieldError(TeamFormFields.TeamLead,
                $"'{department.Name}' has no lead. Every active team needs one (Case Settings → Teams → Team Rules): choose a lead first.") });

        await RunGuardedAsync(async () =>
        {
            department.IsActive = !department.IsActive;
            department.UpdatedAt = DateTime.UtcNow;
            _context.CaseEvents.Add(Audit("TOGGLE_STATUS", department.Name, $"Toggled team '{department.Name}' status to {(department.IsActive ? "ACTIVE" : "INACTIVE")}", (!department.IsActive).ToString(), department.IsActive.ToString(), currentUserId));
            if (!department.IsActive)
            {
                // An inactive team keeps no departments: they are released (unassigned), never handed to another team.
                var links = await _context.TeamDepartments.Include(l => l.Department).Where(l => l.TeamId == id).ToListAsync(ct);
                if (links.Count > 0)
                {
                    var released = string.Join(", ", links.Select(l => l.Department.Name).OrderBy(n => n));
                    _context.TeamDepartments.RemoveRange(links);
                    _context.CaseEvents.Add(Audit("UPDATE", department.Name, $"Deactivated team '{department.Name}': released {released} (now unassigned)", released, "Unassigned", currentUserId));
                }
            }
            await _context.SaveChangesAsync(ct);
        }, ct);

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
