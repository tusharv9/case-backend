namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public interface IAssignmentSettingsService
{
    Task<AssignmentSettingsDto> GetAsync(CancellationToken ct = default);
    Task<AssignmentSettingsDto> SaveAsync(UpdateAssignmentSettingsDto dto, Guid actingUserId, CancellationToken ct = default);
}

/// <summary>
/// Reads and saves the Assign &amp; Reassign settings. The policy row, the cross-team list, the members' "can be chosen" flags and
/// the global automatic-assignment row are saved together or not at all. The global row is changed through the routing engine,
/// so its validation exists once. Saving clears the configuration cache (ConfigChangeInterceptor): the next assignment uses it.
/// </summary>
public class AssignmentSettingsService : IAssignmentSettingsService
{
    private readonly AppDbContext _context;
    private readonly IRoutingEngineService _routing;

    public AssignmentSettingsService(AppDbContext context, IRoutingEngineService routing)
    {
        _context = context;
        _routing = routing;
    }

    public async Task<AssignmentSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var settings = await LoadSettingsAsync(tracking: false, ct);
        var global = await _routing.GetAssignmentConfigAsync(null, ct);
        var vocabulary = await _routing.GetVocabularyAsync(ct);

        var teamConfigs = await _context.AssignmentConfigurations.AsNoTracking()
            .Where(c => c.IsActive && c.DepartmentId != null)
            .ToDictionaryAsync(c => c.DepartmentId!.Value, c => c.MaxConcurrentCapacity, ct);

        var teams = await _context.Departments.AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new AssignmentSettingsTeamDto
            {
                Id = d.Id,
                Name = d.Name,
                Code = d.Code,
                IsActive = d.IsActive,
                LeadId = d.OwnerId,
                LeadName = d.Owner != null ? d.Owner.Name : null,
                Members = d.Members
                    .Where(m => m.IsActive)
                    .OrderBy(m => m.User.Name)
                    .Select(m => new AssignmentSettingsMemberDto
                    {
                        UserId = m.UserId,
                        Name = m.User.Name,
                        Role = m.MemberRole,
                        Status = m.User.Status.ToString(),
                        IsUserActive = m.User.IsActive,
                        IsLead = d.OwnerId == m.UserId,
                        IsAssignable = m.IsAssignable,
                        CanBeManuallyAssigned = m.CanBeManuallyAssigned,
                    })
                    .ToList(),
            })
            .ToListAsync(ct);

        foreach (var t in teams)
        {
            t.HasOwnRoutingSettings = teamConfigs.ContainsKey(t.Id);
            t.Capacity = teamConfigs.TryGetValue(t.Id, out var own) ? own : global.MaxConcurrentCapacity;
        }

        return new AssignmentSettingsDto
        {
            CrossTeamMode = settings.CrossTeamMode,
            CrossTeamDepartmentIds = settings.CrossTeams.Select(c => c.DepartmentId).ToList(),
            CrossTeamModes = CrossTeamModes.All.ToList(),
            IncludeTeamLeads = settings.IncludeTeamLeads,
            RequireAvailable = settings.RequireAvailable,
            EnforceCapacity = settings.EnforceCapacity,
            DefaultAlgorithm = global.Algorithm,
            DefaultCapacity = global.MaxConcurrentCapacity,
            Algorithms = vocabulary.Algorithms,
            Teams = teams,
            Version = VersionOf(settings),
        };
    }

    public async Task<AssignmentSettingsDto> SaveAsync(UpdateAssignmentSettingsDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        var errors = new List<FieldError>();

        var mode = CrossTeamModes.All.FirstOrDefault(m => string.Equals(m, dto.CrossTeamMode, StringComparison.OrdinalIgnoreCase));
        if (mode == null)
            errors.Add(new FieldError("crossTeamMode", $"'{dto.CrossTeamMode}' is not a cross-team option. Choose one of: {string.Join(", ", CrossTeamModes.All)}."));

        var crossTeamIds = dto.CrossTeamDepartmentIds.Distinct().ToList();
        if (mode == CrossTeamModes.SelectedTeams)
        {
            if (crossTeamIds.Count == 0)
                errors.Add(new FieldError("crossTeamDepartmentIds", "Choose at least one team whose members may receive other teams' cases, or turn cross-team assignment off."));
            var activeIds = await _context.Departments.AsNoTracking().Where(d => crossTeamIds.Contains(d.Id) && d.IsActive).Select(d => d.Id).ToListAsync(ct);
            if (activeIds.Count != crossTeamIds.Count)
                errors.Add(new FieldError("crossTeamDepartmentIds", "Some of the chosen teams no longer exist or are inactive. Reload and choose again."));
        }
        else
        {
            crossTeamIds.Clear();   // only meaningful for SelectedTeams
        }

        // Member selections must point at current, active membership rows.
        var selections = dto.MemberSelections
            .GroupBy(s => (s.DepartmentId, s.UserId)).Select(g => g.Last()).ToList();
        var keys = selections.Select(s => s.DepartmentId).Distinct().ToList();
        var rows = await _context.TeamMembers
            .Where(m => keys.Contains(m.DepartmentId) && m.IsActive)
            .ToListAsync(ct);
        var unknown = selections.Where(s => !rows.Any(r => r.DepartmentId == s.DepartmentId && r.UserId == s.UserId)).ToList();
        if (unknown.Count > 0)
            errors.Add(new FieldError("memberSelections", $"{unknown.Count} selected member(s) are no longer in that team. Reload and choose again."));

        if (errors.Count > 0) throw new FieldValidationException(errors);

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(ct);

            var settings = await LoadSettingsAsync(tracking: true, ct);
            if (dto.ExpectedVersion.HasValue && !SameInstant(dto.ExpectedVersion.Value, VersionOf(settings)))
                throw new DbUpdateConcurrencyException("The Assign & Reassign settings were saved by someone else since they were loaded.");

            // The default routing strategy is the existing global row, changed through the routing engine so its validation and audit
            // exist once. First, so an invalid algorithm or capacity stops the save before anything else is written.
            var global = await _routing.GetAssignmentConfigAsync(null, ct);
            if (!string.Equals(global.Algorithm, dto.DefaultAlgorithm, StringComparison.OrdinalIgnoreCase) || global.MaxConcurrentCapacity != dto.DefaultCapacity)
            {
                await _routing.UpdateAssignmentConfigAsync(
                    new UpdateAssignmentConfigDto { Algorithm = dto.DefaultAlgorithm, MaxConcurrentCapacity = dto.DefaultCapacity }, actingUserId, null, ct);
            }

            var before = Describe(settings.IncludeTeamLeads, settings.RequireAvailable, settings.EnforceCapacity, settings.CrossTeamMode, settings.CrossTeams.Count);

            settings.IncludeTeamLeads = dto.IncludeTeamLeads;
            settings.RequireAvailable = dto.RequireAvailable;
            settings.EnforceCapacity = dto.EnforceCapacity;
            settings.CrossTeamMode = mode!;
            settings.UpdatedAt = DateTime.UtcNow;

            _context.ManualAssignmentCrossTeams.RemoveRange(settings.CrossTeams.Where(c => !crossTeamIds.Contains(c.DepartmentId)));
            foreach (var id in crossTeamIds.Where(id => settings.CrossTeams.All(c => c.DepartmentId != id)))
                _context.ManualAssignmentCrossTeams.Add(new ManualAssignmentCrossTeam { SettingsId = settings.Id, DepartmentId = id });

            var changedMembers = 0;
            foreach (var s in selections)
            {
                var row = rows.First(r => r.DepartmentId == s.DepartmentId && r.UserId == s.UserId);
                if (row.CanBeManuallyAssigned == s.CanBeManuallyAssigned) continue;
                row.CanBeManuallyAssigned = s.CanBeManuallyAssigned;
                row.UpdatedAt = DateTime.UtcNow;
                changedMembers++;
            }

            _context.CaseEvents.Add(new CaseEvent
            {
                Id = Guid.NewGuid(),
                EventType = EventType.Other,
                Message = $"Assign & Reassign settings updated{(changedMembers > 0 ? $"; {changedMembers} member selection(s) changed" : "")}.",
                CreatedAt = DateTime.UtcNow,
                UserId = actingUserId,
                Module = "Configurable Settings",
                EntityName = "Assign & Reassign settings",
                ActionType = "UPDATE",
                OldValue = before,
                NewValue = Describe(dto.IncludeTeamLeads, dto.RequireAvailable, dto.EnforceCapacity, mode!, crossTeamIds.Count),
            });
            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        _context.ChangeTracker.Clear();
        return await GetAsync(ct);
    }

    private async Task<ManualAssignmentSettings> LoadSettingsAsync(bool tracking, CancellationToken ct)
    {
        var query = _context.ManualAssignmentSettings.Include(m => m.CrossTeams).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The Assign & Reassign settings are missing. Apply the database migrations.");
    }

    private static DateTime VersionOf(ManualAssignmentSettings s) => DateTime.SpecifyKind(s.UpdatedAt ?? s.CreatedAt, DateTimeKind.Utc);

    // The database keeps microseconds; a browser round trip may drop sub-millisecond digits.
    private static bool SameInstant(DateTime a, DateTime b) =>
        Math.Abs((a.ToUniversalTime() - b.ToUniversalTime()).TotalMilliseconds) < 1;

    private static string Describe(bool leads, bool available, bool capacity, string mode, int crossTeams) =>
        $"Team leads: {(leads ? "eligible" : "excluded")}; Available/Busy only: {available}; Capacity enforced: {capacity}; Cross-team: {mode}" +
        (mode == CrossTeamModes.SelectedTeams ? $" ({crossTeams} team(s))" : "");
}
