namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public interface ITeamSettingsService
{
    Task<TeamSettingsDto> GetAsync(CancellationToken ct = default);
    Task<TeamSettingsDto> SaveAsync(UpdateTeamSettingsDto dto, Guid actingUserId, CancellationToken ct = default);

    /// <summary>The stored row (or the defaults when it does not exist yet), for the checks <see cref="TeamService"/> runs on every save.</summary>
    Task<TeamSettings> GetPolicyAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads and saves the team rules (Case Settings → Teams → Team Rules). A policy can only be tightened when the existing teams already
/// satisfy it: turning "several teams per person" off while two people are in two teams is refused with their names, rather than
/// silently removing anyone from a team. Saving is guarded by the loaded version, so a newer save is never overwritten.
/// </summary>
public class TeamSettingsService : ITeamSettingsService
{
    private const int NamesShown = 5;
    private readonly AppDbContext _context;

    public TeamSettingsService(AppDbContext context) => _context = context;

    public async Task<TeamSettings> GetPolicyAsync(CancellationToken ct = default) =>
        await _context.TeamSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new TeamSettings { CreatedAt = DateTime.UnixEpoch };

    public async Task<TeamSettingsDto> GetAsync(CancellationToken ct = default) => ToDto(await GetPolicyAsync(ct));

    public async Task<TeamSettingsDto> SaveAsync(UpdateTeamSettingsDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        var errors = new List<FieldError>();
        bool Need(bool? value, string field) { if (value == null) errors.Add(new FieldError(field, "Send every rule's value.")); return value ?? false; }
        var wanted = new TeamSettings
        {
            AllowMultipleTeamMembership = Need(dto.AllowMultipleTeamMembership, "allowMultipleTeamMembership"),
            AllowTeamsWithoutMembers = Need(dto.AllowTeamsWithoutMembers, "allowTeamsWithoutMembers"),
            AllowMultipleTeamLeads = Need(dto.AllowMultipleTeamLeads, "allowMultipleTeamLeads"),
            AllowLeadInOtherTeams = Need(dto.AllowLeadInOtherTeams, "allowLeadInOtherTeams"),
            RequireTeamLead = Need(dto.RequireTeamLead, "requireTeamLead"),
            AllowMultipleDepartments = Need(dto.AllowMultipleDepartments, "allowMultipleDepartments"),
            AllowDeactivateWithOpenCases = Need(dto.AllowDeactivateWithOpenCases, "allowDeactivateWithOpenCases"),
        };
        if (errors.Count > 0) throw new FieldValidationException(errors);

        await TeamWriteLock.RunAsync(_context, async () =>
        {
            var settings = await _context.TeamSettings.FirstOrDefaultAsync(ct);
            if (settings != null && dto.ExpectedVersion.HasValue && !SameInstant(dto.ExpectedVersion.Value, VersionOf(settings)))
                throw new DbUpdateConcurrencyException("The Teams settings were saved by someone else since they were loaded.");
            var current = settings ?? new TeamSettings();

            // Tightening a policy must already be true of the teams as they are. Only a rule being tightened is checked, so an
            // existing exception that was allowed before does not block saving an unrelated rule.
            if (current.AllowMultipleTeamMembership && !wanted.AllowMultipleTeamMembership)
            {
                var people = await PeopleInSeveralTeamsAsync(ct);
                if (people.Count > 0)
                    errors.Add(new FieldError("allowMultipleTeamMembership",
                        $"{Names(people)} {(people.Count == 1 ? "is" : "are")} a member of more than one team. Remove them from the extra teams on the Teams page first."));
            }
            if (current.AllowTeamsWithoutMembers && !wanted.AllowTeamsWithoutMembers)
            {
                var teams = await _context.Departments.AsNoTracking()
                    .Where(d => d.IsActive && !d.Members.Any(m => m.IsActive && m.UserId != d.OwnerId))
                    .OrderBy(d => d.Name).Select(d => d.Name).ToListAsync(ct);
                if (teams.Count > 0)
                    errors.Add(new FieldError("allowTeamsWithoutMembers",
                        $"{Names(teams)} {(teams.Count == 1 ? "has" : "have")} no members. Add members or deactivate {(teams.Count == 1 ? "it" : "them")} first."));
            }
            if (current.AllowMultipleTeamLeads && !wanted.AllowMultipleTeamLeads)
            {
                var leads = await PeopleLeadingSeveralTeamsAsync(ct);
                if (leads.Count > 0)
                    errors.Add(new FieldError("allowMultipleTeamLeads",
                        $"{Names(leads)} {(leads.Count == 1 ? "leads" : "lead")} more than one team. Give those teams another lead first."));
            }
            if (current.AllowLeadInOtherTeams && !wanted.AllowLeadInOtherTeams)
            {
                var leads = await LeadsInOtherTeamsAsync(ct);
                if (leads.Count > 0)
                    errors.Add(new FieldError("allowLeadInOtherTeams",
                        $"{Names(leads)} {(leads.Count == 1 ? "leads a team and is" : "lead a team and are")} also a member of another. Remove them from the other teams first."));
            }
            if (!current.RequireTeamLead && wanted.RequireTeamLead)
            {
                var teams = await _context.Departments.AsNoTracking().Where(d => d.IsActive && d.OwnerId == null).OrderBy(d => d.Name).Select(d => d.Name).ToListAsync(ct);
                if (teams.Count > 0)
                    errors.Add(new FieldError("requireTeamLead",
                        $"{Names(teams)} {(teams.Count == 1 ? "has" : "have")} no lead. Choose a lead for {(teams.Count == 1 ? "it" : "each")} first."));
            }
            if (current.AllowMultipleDepartments && !wanted.AllowMultipleDepartments)
            {
                var teams = await _context.Departments.AsNoTracking().Where(d => d.OrgDepartments.Count > 1).OrderBy(d => d.Name).Select(d => d.Name).ToListAsync(ct);
                if (teams.Count > 0)
                    errors.Add(new FieldError("allowMultipleDepartments",
                        $"{Names(teams)} {(teams.Count == 1 ? "serves" : "serve")} more than one department. Keep one department per team first."));
            }
            // Turning "deactivate with open cases" off changes nothing that exists: inactive teams stay as they are.
            if (errors.Count > 0) throw new FieldValidationException(errors);

            var before = settings == null ? "Defaults" : Describe(settings);
            if (settings == null)
            {
                settings = new TeamSettings { CreatedAt = DateTime.UtcNow };
                _context.TeamSettings.Add(settings);
            }
            settings.AllowMultipleTeamMembership = wanted.AllowMultipleTeamMembership;
            settings.AllowTeamsWithoutMembers = wanted.AllowTeamsWithoutMembers;
            settings.AllowMultipleTeamLeads = wanted.AllowMultipleTeamLeads;
            settings.AllowLeadInOtherTeams = wanted.AllowLeadInOtherTeams;
            settings.RequireTeamLead = wanted.RequireTeamLead;
            settings.AllowMultipleDepartments = wanted.AllowMultipleDepartments;
            settings.AllowDeactivateWithOpenCases = wanted.AllowDeactivateWithOpenCases;
            settings.UpdatedAt = DateTime.UtcNow;

            _context.CaseEvents.Add(new CaseEvent
            {
                Id = Guid.NewGuid(), EventType = EventType.Other, CreatedAt = DateTime.UtcNow, UserId = actingUserId,
                Message = "Team rules updated.", Module = "Teams", EntityName = "Team rules", ActionType = "UPDATE_CONFIG",
                OldValue = before, NewValue = Describe(settings),
            });
            await _context.SaveChangesAsync(ct);
        }, ct);

        _context.ChangeTracker.Clear();
        return await GetAsync(ct);
    }

    /// <summary>People who lead one team and hold a member row in a different team.</summary>
    private Task<List<string>> LeadsInOtherTeamsAsync(CancellationToken ct) =>
        _context.TeamMembers.AsNoTracking()
            .Where(m => m.IsActive && m.Department.OwnerId != m.UserId && _context.Departments.Any(d => d.OwnerId == m.UserId))
            .Select(m => m.User.Name).Distinct().OrderBy(n => n).ToListAsync(ct);

    /// <summary>People with an active membership in two or more teams, not counting the member row a lead has in the team they lead.</summary>
    private Task<List<string>> PeopleInSeveralTeamsAsync(CancellationToken ct) =>
        _context.TeamMembers.AsNoTracking()
            .Where(m => m.IsActive && m.Department.OwnerId != m.UserId)
            .GroupBy(m => new { m.UserId, m.User.Name })
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.Name).OrderBy(n => n).ToListAsync(ct);

    private Task<List<string>> PeopleLeadingSeveralTeamsAsync(CancellationToken ct) =>
        _context.Departments.AsNoTracking()
            .Where(d => d.OwnerId != null)
            .GroupBy(d => new { d.OwnerId, d.Owner!.Name })
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.Name).OrderBy(n => n).ToListAsync(ct);

    private static string Names(IReadOnlyList<string> names) =>
        names.Count <= NamesShown ? string.Join(", ", names) : $"{string.Join(", ", names.Take(NamesShown))} and {names.Count - NamesShown} more";

    private static TeamSettingsDto ToDto(TeamSettings s) => new()
    {
        AllowMultipleTeamMembership = s.AllowMultipleTeamMembership,
        AllowMultipleTeamLeads = s.AllowMultipleTeamLeads,
        AllowTeamsWithoutMembers = s.AllowTeamsWithoutMembers,
        AllowLeadInOtherTeams = s.AllowLeadInOtherTeams,
        RequireTeamLead = s.RequireTeamLead,
        AllowMultipleDepartments = s.AllowMultipleDepartments,
        AllowDeactivateWithOpenCases = s.AllowDeactivateWithOpenCases,
        Version = VersionOf(s),
    };

    private static DateTime VersionOf(TeamSettings s) => DateTime.SpecifyKind(s.UpdatedAt ?? s.CreatedAt, DateTimeKind.Utc);

    // The database keeps microseconds; a browser round trip may drop sub-millisecond digits.
    private static bool SameInstant(DateTime a, DateTime b) =>
        Math.Abs((a.ToUniversalTime() - b.ToUniversalTime()).TotalMilliseconds) < 1;

    private static string Describe(TeamSettings s) =>
        $"Several teams per person: {s.AllowMultipleTeamMembership}; Teams without members: {s.AllowTeamsWithoutMembers}; " +
        $"Several teams per lead: {s.AllowMultipleTeamLeads}; Lead also in other teams: {s.AllowLeadInOtherTeams}; Lead required: {s.RequireTeamLead}; " +
        $"Several departments per team: {s.AllowMultipleDepartments}; Deactivate with open cases: {s.AllowDeactivateWithOpenCases}";
}
