namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Teams;
using Microsoft.EntityFrameworkCore;

public interface IDepartmentService
{
    Task<List<DepartmentDto>> ListAsync(bool includeInactive, CancellationToken ct = default);
    Task<DepartmentDto> CreateAsync(SaveDepartmentDto dto, Guid actingUserId, CancellationToken ct = default);
    Task<DepartmentDto> UpdateAsync(Guid id, SaveDepartmentDto dto, Guid actingUserId, CancellationToken ct = default);
    /// <summary>Deletes a department nothing refers to; one still mapped to a team, or with cases or sub-categories, is refused.</summary>
    Task DeleteAsync(Guid id, Guid actingUserId, CancellationToken ct = default);
    /// <summary>Puts the departments in the given order (the order every Department dropdown shows).</summary>
    Task<List<DepartmentDto>> ReorderAsync(IReadOnlyList<Guid> ids, Guid actingUserId, CancellationToken ct = default);
    /// <summary>
    /// Assigns the department to a team, or (null) removes its assignment. A department already assigned to a different team
    /// is refused, naming that team: it is never moved silently.
    /// </summary>
    Task<DepartmentDto> AssignTeamAsync(Guid id, Guid? teamId, Guid actingUserId, CancellationToken ct = default);
}

/// <summary>
/// The departments: the master list behind every Department dropdown (Create Case, Create/Edit Team, routing rules, filters).
/// A department is independent of teams; it is mapped to at most one team (see <see cref="DepartmentTeams"/>), from the team's
/// form or from here. Creating a department never creates or picks a team. Changes are saved at once, audited, and clear the
/// configuration cache, so every form shows them on its next read.
/// </summary>
public class DepartmentService : IDepartmentService
{
    private readonly AppDbContext _context;
    private readonly ITeamSettingsService? _settings;

    public DepartmentService(AppDbContext context, ITeamSettingsService? settings = null)
    {
        _context = context;
        _settings = settings;
    }

    public async Task<List<DepartmentDto>> ListAsync(bool includeInactive, CancellationToken ct = default)
    {
        var departments = await _context.Departments.AsNoTracking()
            .Where(d => includeInactive || d.IsActive)
            .OrderBy(d => d.DisplayOrder).ThenBy(d => d.Name)
            .Select(d => new DepartmentDto
            {
                Id = d.Id, Name = d.Name, Code = d.Code, Description = d.Description, IsActive = d.IsActive,
                DisplayOrder = d.DisplayOrder,
                Team = d.TeamLinks.Select(l => new DepartmentTeamDto { Id = l.TeamId, Name = l.Team.Name, IsActive = l.Team.IsActive }).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var ids = departments.Select(d => d.Id).ToList();
        var cases = await _context.Cases.AsNoTracking().Where(c => c.DepartmentId != null && ids.Contains(c.DepartmentId.Value))
            .GroupBy(c => c.DepartmentId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        foreach (var d in departments) d.CaseCount = cases.GetValueOrDefault(d.Id);
        return departments;
    }

    public async Task<List<DepartmentDto>> ReorderAsync(IReadOnlyList<Guid> ids, Guid actingUserId, CancellationToken ct = default)
    {
        var all = await _context.Departments.ToListAsync(ct);
        if (ids.Count != all.Count || ids.Distinct().Count() != ids.Count || ids.Any(i => all.All(d => d.Id != i)))
            throw new FieldValidationException(new[] { new FieldError("ids", "The list of departments has changed since it was loaded. Reload and try again.") });
        for (var i = 0; i < ids.Count; i++)
        {
            var d = all.First(x => x.Id == ids[i]);
            if (d.DisplayOrder != i + 1) { d.DisplayOrder = i + 1; d.UpdatedAt = DateTime.UtcNow; }
        }
        _context.CaseEvents.Add(Audit("UPDATE", "Department order", "Reordered departments", null,
            string.Join(", ", ids.Select(i => all.First(d => d.Id == i).Name)), actingUserId));
        await _context.SaveChangesAsync(ct);
        return await ListAsync(true, ct);
    }

    public async Task<DepartmentDto> AssignTeamAsync(Guid id, Guid? teamId, Guid actingUserId, CancellationToken ct = default)
    {
        var department = await _context.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new KeyNotFoundException("Department not found.");
        Team? team = null;
        if (teamId is Guid wanted)
        {
            team = await _context.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == wanted, ct)
                ?? throw new FieldValidationException(new[] { new FieldError("teamId", "That team no longer exists. Reload and choose again.") });
            if (!team.IsActive)
                throw new FieldValidationException(new[] { new FieldError("teamId", $"'{team.Name}' is inactive. Activate it before assigning departments to it.") });
            if (!department.IsActive)
                throw new FieldValidationException(new[] { new FieldError("teamId", $"{department.Name} is inactive. Activate it before assigning it to a team.") });
        }

        try
        {
            await TeamWriteLock.RunAsync(_context, async () =>
            {
                var current = await _context.TeamDepartments.Include(l => l.Team).FirstOrDefaultAsync(l => l.DepartmentId == id, ct);
                if (current?.TeamId == teamId) return;                       // already so: nothing to change
                if (team != null)
                {
                    await DepartmentTeams.EnsureNotMappedElsewhereAsync(_context, new[] { id }, team.Id, "teamId", ct);
                    var policy = _settings != null ? await _settings.GetPolicyAsync(ct) : null;
                    if (policy is { AllowMultipleDepartments: false } && await _context.TeamDepartments.AnyAsync(l => l.TeamId == team.Id, ct))
                        throw new FieldValidationException(new[] { new FieldError("teamId",
                            $"'{team.Name}' already handles a department, and a team may handle only one (Case Settings → Teams → Team Rules).") });
                    _context.TeamDepartments.Add(new TeamDepartment { TeamId = team.Id, DepartmentId = id, LinkedAt = DateTime.UtcNow });
                    _context.CaseEvents.Add(Audit("UPDATE", department.Name, $"Assigned department '{department.Name}' to team '{team.Name}'", "Unassigned", team.Name, actingUserId));
                }
                else if (current != null)
                {
                    _context.TeamDepartments.Remove(current);
                    _context.CaseEvents.Add(Audit("UPDATE", department.Name, $"Removed department '{department.Name}' from team '{current.Team.Name}'", current.Team.Name, "Unassigned", actingUserId));
                }
                await _context.SaveChangesAsync(ct);
            }, ct);
        }
        catch (DbUpdateException ex) when (DepartmentTeams.IsDuplicateMapping(ex))
        {
            throw new FieldValidationException(new[] { new FieldError("teamId", DepartmentTeams.RaceMessage) });
        }
        return (await ListAsync(true, ct)).First(d => d.Id == id);
    }

    public async Task<DepartmentDto> CreateAsync(SaveDepartmentDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        var (name, code) = await ValidateAsync(null, dto, ct);
        var nextOrder = (await _context.Departments.MaxAsync(d => (int?)d.DisplayOrder, ct) ?? 0) + 1;
        var department = new Department
        {
            Name = name, Code = code, Description = Clean(dto.Description), IsActive = dto.IsActive, DisplayOrder = nextOrder, CreatedAt = DateTime.UtcNow,
        };
        _context.Departments.Add(department);
        _context.CaseEvents.Add(Audit("CREATE", name, $"Created department '{name}'", null, $"{name} ({code})", actingUserId));
        await _context.SaveChangesAsync(ct);
        return (await ListAsync(true, ct)).First(d => d.Id == department.Id);
    }

    public async Task<DepartmentDto> UpdateAsync(Guid id, SaveDepartmentDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        var department = await _context.Departments.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new KeyNotFoundException("Department not found.");
        var (name, code) = await ValidateAsync(id, dto, ct);
        var before = $"{department.Name} ({department.Code}); active: {department.IsActive}";

        department.Name = name;
        department.Code = code;
        department.Description = Clean(dto.Description);
        department.IsActive = dto.IsActive;
        department.UpdatedAt = DateTime.UtcNow;

        _context.CaseEvents.Add(Audit("UPDATE", name, $"Updated department '{name}'", before, $"{name} ({code}); active: {dto.IsActive}", actingUserId));
        await _context.SaveChangesAsync(ct);
        return (await ListAsync(true, ct)).First(d => d.Id == id);
    }

    public async Task DeleteAsync(Guid id, Guid actingUserId, CancellationToken ct = default)
    {
        var department = await _context.Departments.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new KeyNotFoundException("Department not found.");
        var teams = await _context.TeamDepartments.AsNoTracking().Where(l => l.DepartmentId == id).Select(l => l.Team.Name).OrderBy(n => n).ToListAsync(ct);
        if (teams.Count > 0)
            throw new InvalidOperationException($"'{department.Name}' is assigned to the team '{teams[0]}'. Remove it from that team first, or deactivate the department instead.");
        var cases = await _context.Cases.CountAsync(c => c.DepartmentId == id, ct);
        if (cases > 0)
            throw new InvalidOperationException($"{cases} {(cases == 1 ? "case belongs" : "cases belong")} to '{department.Name}', so it cannot be deleted. Deactivate it instead: its cases keep it, and it is no longer offered for new ones.");
        var subCategories = await _context.DepartmentSubCategories.CountAsync(s => s.DepartmentId == id, ct);
        if (subCategories > 0)
            throw new InvalidOperationException($"'{department.Name}' has {subCategories} sub-{(subCategories == 1 ? "category" : "categories")}. Deactivate the department instead, or remove its sub-categories first.");

        _context.Departments.Remove(department);
        _context.CaseEvents.Add(Audit("DELETE", department.Name, $"Deleted department '{department.Name}'", department.Name, null, actingUserId));
        await _context.SaveChangesAsync(ct);
    }

    private async Task<(string Name, string Code)> ValidateAsync(Guid? id, SaveDepartmentDto dto, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        var name = (dto.Name ?? string.Empty).Trim();
        var code = string.IsNullOrWhiteSpace(dto.Code) ? CodeFrom(name) : dto.Code.Trim().ToUpperInvariant();

        if (name.Length == 0) errors.Add(new FieldError("name", "Department name is required."));
        else if (name.Length > 120) errors.Add(new FieldError("name", "Department name can be at most 120 characters."));
        if (code.Length > 20) errors.Add(new FieldError("code", "Department code can be at most 20 characters."));
        if ((dto.Description ?? string.Empty).Trim().Length > 500) errors.Add(new FieldError("description", "Description can be at most 500 characters."));

        if (name.Length > 0)
        {
            var lowered = name.ToLower();
            if (await _context.Departments.AnyAsync(d => d.Id != id && d.Name.ToLower() == lowered, ct))
                errors.Add(new FieldError("name", $"A department named '{name}' already exists."));
        }
        if (code.Length > 0)
        {
            var upper = code.ToUpper();
            if (await _context.Departments.AnyAsync(d => d.Id != id && d.Code.ToUpper() == upper, ct))
                errors.Add(new FieldError("code", $"The code '{code}' is already used by another department."));
        }
        if (errors.Count > 0) throw new FieldValidationException(errors);
        return (name, code);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Initials of the words (up to five), or the first three letters of a single word.</summary>
    internal static string CodeFrom(string name)
    {
        var words = name.Split(new[] { ' ', '-', '_', '&', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return string.Empty;
        if (words.Length == 1) return (words[0].Length >= 3 ? words[0][..3] : words[0]).ToUpperInvariant();
        var initials = string.Concat(words.Select(w => w[0])).ToUpperInvariant();
        return initials.Length > 5 ? initials[..5] : initials;
    }

    private static CaseEvent Audit(string action, string entity, string message, string? oldValue, string? newValue, Guid userId) => new()
    {
        Id = Guid.NewGuid(), EventType = EventType.Other, CreatedAt = DateTime.UtcNow, UserId = userId, Message = message,
        Module = "Departments", EntityName = entity, ActionType = action, OldValue = oldValue, NewValue = newValue,
    };
}
