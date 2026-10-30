namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public interface IOrgDepartmentService
{
    Task<List<OrgDepartmentDto>> ListAsync(bool includeInactive, CancellationToken ct = default);
    Task<OrgDepartmentDto> CreateAsync(SaveOrgDepartmentDto dto, Guid actingUserId, CancellationToken ct = default);
    Task<OrgDepartmentDto> UpdateAsync(Guid id, SaveOrgDepartmentDto dto, Guid actingUserId, CancellationToken ct = default);
    /// <summary>Deletes a department nothing refers to; one still linked to a team, case or sub-category is refused (deactivate it instead).</summary>
    Task DeleteAsync(Guid id, Guid actingUserId, CancellationToken ct = default);
    /// <summary>Puts the departments in the given order (the order every Department dropdown shows).</summary>
    Task<List<OrgDepartmentDto>> ReorderAsync(IReadOnlyList<Guid> ids, Guid actingUserId, CancellationToken ct = default);
    /// <summary>Makes a serving team the department's default (where its cases go when no routing rule matches), or clears it.</summary>
    Task<OrgDepartmentDto> SetDefaultTeamAsync(Guid id, Guid? teamId, Guid actingUserId, CancellationToken ct = default);
}

/// <summary>
/// The business departments: the master list behind every Department dropdown (Create Case, Create/Edit Team, routing rules,
/// filters). Teams are linked to the departments they serve; cases and sub-categories belong to one. Changes are saved at once,
/// audited, and clear the configuration cache, so every form shows them on its next read.
/// </summary>
public class OrgDepartmentService : IOrgDepartmentService
{
    private readonly AppDbContext _context;

    public OrgDepartmentService(AppDbContext context) => _context = context;

    public async Task<List<OrgDepartmentDto>> ListAsync(bool includeInactive, CancellationToken ct = default)
    {
        var departments = await _context.OrgDepartments.AsNoTracking()
            .Where(d => includeInactive || d.IsActive)
            .OrderBy(d => d.DisplayOrder).ThenBy(d => d.Name)
            .Select(d => new OrgDepartmentDto
            {
                Id = d.Id, Name = d.Name, Code = d.Code, Description = d.Description, IsActive = d.IsActive,
                DisplayOrder = d.DisplayOrder, TeamCount = d.Teams.Count,
            })
            .ToListAsync(ct);
        var ids = departments.Select(d => d.Id).ToList();
        var links = await _context.TeamDepartments.AsNoTracking().Where(l => ids.Contains(l.OrgDepartmentId))
            .OrderBy(l => l.Team.Name)
            .Select(l => new { l.OrgDepartmentId, Team = new DepartmentTeamDto { Id = l.TeamId, Name = l.Team.Name, IsActive = l.Team.IsActive, IsDefault = l.IsDefault } })
            .ToListAsync(ct);
        var cases = await _context.Cases.AsNoTracking().Where(c => c.OrgDepartmentId != null && ids.Contains(c.OrgDepartmentId.Value))
            .GroupBy(c => c.OrgDepartmentId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        foreach (var d in departments)
        {
            d.Teams = links.Where(l => l.OrgDepartmentId == d.Id).Select(l => l.Team).ToList();
            d.CaseCount = cases.GetValueOrDefault(d.Id);
        }
        return departments;
    }

    public async Task<List<OrgDepartmentDto>> ReorderAsync(IReadOnlyList<Guid> ids, Guid actingUserId, CancellationToken ct = default)
    {
        var all = await _context.OrgDepartments.ToListAsync(ct);
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

    public async Task<OrgDepartmentDto> SetDefaultTeamAsync(Guid id, Guid? teamId, Guid actingUserId, CancellationToken ct = default)
    {
        var department = await _context.OrgDepartments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new KeyNotFoundException("Department not found.");
        var links = await _context.TeamDepartments.Include(l => l.Team).Where(l => l.OrgDepartmentId == id).ToListAsync(ct);
        var chosen = teamId == null ? null : links.FirstOrDefault(l => l.TeamId == teamId)
            ?? throw new FieldValidationException(new[] { new FieldError("teamId", "That team does not serve this department. Link it to the department on the team first.") });
        if (chosen is { Team.IsActive: false })
            throw new FieldValidationException(new[] { new FieldError("teamId", $"'{chosen.Team.Name}' is inactive, so it cannot receive the department's cases.") });

        var before = links.FirstOrDefault(l => l.IsDefault)?.Team.Name ?? "none";
        await TeamWriteLock.RunAsync(_context, async () =>
        {
            // Clear first, then set: the partial unique index allows one default per department at any moment.
            foreach (var l in links.Where(l => l.IsDefault && l != chosen)) l.IsDefault = false;
            await _context.SaveChangesAsync(ct);
            if (chosen != null) chosen.IsDefault = true;
            _context.CaseEvents.Add(Audit("UPDATE", department.Name, $"Default team of '{department.Name}' set to {chosen?.Team.Name ?? "none"}", before, chosen?.Team.Name ?? "none", actingUserId));
            await _context.SaveChangesAsync(ct);
        }, ct);
        return (await ListAsync(true, ct)).First(d => d.Id == id);
    }

    public async Task<OrgDepartmentDto> CreateAsync(SaveOrgDepartmentDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        var (name, code) = await ValidateAsync(null, dto, ct);
        var nextOrder = (await _context.OrgDepartments.MaxAsync(d => (int?)d.DisplayOrder, ct) ?? 0) + 1;
        var department = new OrgDepartment
        {
            Name = name, Code = code, Description = Clean(dto.Description), IsActive = dto.IsActive, DisplayOrder = nextOrder, CreatedAt = DateTime.UtcNow,
        };
        _context.OrgDepartments.Add(department);
        _context.CaseEvents.Add(Audit("CREATE", name, $"Created department '{name}'", null, $"{name} ({code})", actingUserId));
        await _context.SaveChangesAsync(ct);
        return (await ListAsync(true, ct)).First(d => d.Id == department.Id);
    }

    public async Task<OrgDepartmentDto> UpdateAsync(Guid id, SaveOrgDepartmentDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        var department = await _context.OrgDepartments.FirstOrDefaultAsync(d => d.Id == id, ct)
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
        var department = await _context.OrgDepartments.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new KeyNotFoundException("Department not found.");
        var teams = await _context.TeamDepartments.AsNoTracking().Where(l => l.OrgDepartmentId == id).Select(l => l.Team.Name).OrderBy(n => n).ToListAsync(ct);
        if (teams.Count > 0)
            throw new InvalidOperationException($"'{department.Name}' is served by {string.Join(", ", teams)}. Remove it from those teams, or deactivate it instead.");
        var cases = await _context.Cases.CountAsync(c => c.OrgDepartmentId == id, ct);
        if (cases > 0)
            throw new InvalidOperationException($"{cases} {(cases == 1 ? "case belongs" : "cases belong")} to '{department.Name}', so it cannot be deleted. Deactivate it instead: its cases keep it, and it is no longer offered for new ones.");
        var subCategories = await _context.DepartmentSubCategories.CountAsync(s => s.OrgDepartmentId == id, ct);
        if (subCategories > 0)
            throw new InvalidOperationException($"'{department.Name}' has {subCategories} sub-{(subCategories == 1 ? "category" : "categories")}. Deactivate the department instead, or remove its sub-categories first.");

        _context.OrgDepartments.Remove(department);
        _context.CaseEvents.Add(Audit("DELETE", department.Name, $"Deleted department '{department.Name}'", department.Name, null, actingUserId));
        await _context.SaveChangesAsync(ct);
    }

    private async Task<(string Name, string Code)> ValidateAsync(Guid? id, SaveOrgDepartmentDto dto, CancellationToken ct)
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
            if (await _context.OrgDepartments.AnyAsync(d => d.Id != id && d.Name.ToLower() == lowered, ct))
                errors.Add(new FieldError("name", $"A department named '{name}' already exists."));
        }
        if (code.Length > 0)
        {
            var upper = code.ToUpper();
            if (await _context.OrgDepartments.AnyAsync(d => d.Id != id && d.Code.ToUpper() == upper, ct))
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
