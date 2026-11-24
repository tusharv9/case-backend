namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public class SlaRoutingService : ISlaRoutingService
{
    private readonly AppDbContext _context;
    private readonly ILogger<SlaRoutingService> _logger;
    private readonly IEscalationService _escalation;
    private readonly IConfigCache? _cache;
    private readonly IOrgDirectory? _directory;
    private readonly IDbContextFactory<AppDbContext>? _contexts;
    private readonly IHostTimeZone? _hostZone;

    public SlaRoutingService(AppDbContext context, ILogger<SlaRoutingService> logger, IEscalationService escalation,
        IConfigCache? cache = null, IOrgDirectory? directory = null, IDbContextFactory<AppDbContext>? contexts = null,
        IHostTimeZone? hostZone = null)
    {
        _hostZone = hostZone;
        _context = context;
        _logger = logger;
        _escalation = escalation;
        _cache = cache;
        _directory = directory;
        _contexts = contexts;
    }

    // ===================================================================================== escalation validation

    /// <summary>The roles a DTO level names: its role list, or (an older client) its single TargetRole.</summary>
    private static List<EscalationRoleDto> RolesOf(EscalationLevelConfigDto l) =>
        l.Roles.Count > 0
            ? l.Roles.Where(r => !string.IsNullOrWhiteSpace(r.Name) || !string.IsNullOrWhiteSpace(r.Key)).ToList()
            : string.IsNullOrWhiteSpace(l.TargetRole) ? new() : new() { new EscalationRoleDto { Key = l.TargetRole.Trim(), Name = l.TargetRole.Trim() } };

    private static EscalationLevelRules.Input InputOf(EscalationLevelConfigDto l) =>
        new(l.LevelNumber, RolesOf(l).Select(r => (r.Name ?? r.Key).Trim()).ToList(), l.IsInitial, l.IsInitial ? null : l.TriggerType, l.TriggerValue,
            l.ActionDescription, l.RoutingStrategy, l.TargetUserId, l.FallbackPolicy, l.ManualAllowed);

    /// <summary>
    /// The Host's roles with Case Management access, or null when they cannot be read (a matrix is then checked without them).
    /// </summary>
    private async Task<IReadOnlyList<HostRoleRef>?> HostRolesAsync(CancellationToken ct)
    {
        if (_directory == null) return null;
        try { return await _directory.CaseRoleRefsAsync(ct); }
        catch (HostUsersException) { return null; }
    }

    /// <summary>
    /// Brings each level's roles to the Host's current form (id and name), matching by id, else by name. A role the Host does
    /// not know keeps what was sent, and validation names it.
    /// </summary>
    private static void Canonicalise(IEnumerable<EscalationLevelConfigDto> levels, IReadOnlyList<HostRoleRef>? host)
    {
        foreach (var l in levels)
        {
            l.Roles = RolesOf(l).Select(r =>
            {
                var match = host?.FirstOrDefault(h => h.Key == r.Key) ?? host?.FirstOrDefault(h => string.Equals(h.Name, (r.Name ?? r.Key).Trim(), StringComparison.OrdinalIgnoreCase));
                return match != null ? new EscalationRoleDto { Key = match.Key, Name = match.Name } : new EscalationRoleDto { Key = (r.Key ?? r.Name ?? "").Trim(), Name = (r.Name ?? r.Key ?? "").Trim() };
            }).DistinctBy(r => r.Key).ToList();
            l.TargetRole = string.Join(", ", l.Roles.Select(r => r.Name));
        }
    }

    /// <summary>
    /// Checks the whole matrix as it would be after the change (see <see cref="EscalationLevelRules.Validate"/>), plus that a
    /// "specific person" holds one of the level's roles. Refuses with every problem at once.
    /// </summary>
    private async Task ValidateMatrixAsync(IReadOnlyList<EscalationLevelConfigDto> levels, IReadOnlyList<HostRoleRef>? host, CancellationToken ct)
    {
        var errors = EscalationLevelRules.Validate(levels.Select(InputOf).ToList(), host?.Select(h => h.Name).ToList()).ToList();

        if (_directory != null && host != null)
        {
            foreach (var l in levels.Where(l => !l.IsInitial && EscalationRouting.Canonical(l.RoutingStrategy) == EscalationRouting.SpecificUser && l.TargetUserId is not null))
            {
                var holds = false;
                foreach (var role in l.Roles)
                    if ((await _directory.UsersInRoleRefAsync(new HostRoleRef(role.Key, role.Name), ct)).Any(u => u.Id == l.TargetUserId && u.IsActive)) { holds = true; break; }
                if (!holds)
                    errors.Add(new FieldError($"Level {l.LevelNumber}", $"Level {l.LevelNumber}: the chosen person is not an active holder of {l.TargetRole}."));
            }
        }
        if (errors.Count > 0) throw new FieldValidationException(errors);
    }

    /// <summary>The matrix in the request is the stored one, field for field (what Apply would write).</summary>
    private static bool SameMatrix(IReadOnlyList<EscalationLevelConfig> stored, IReadOnlyList<EscalationLevelConfigDto> input)
    {
        if (stored.Count != input.Count) return false;
        var byNumber = stored.ToDictionary(l => l.LevelNumber);
        foreach (var i in input)
        {
            if (!byNumber.TryGetValue(i.LevelNumber, out var l)) return false;
            var probe = new EscalationLevelConfig { LevelNumber = i.LevelNumber };
            Apply(probe, i);
            if (Signature(probe) != Signature(l)) return false;
        }
        return true;
    }

    private static string Signature(EscalationLevelConfig l) => System.Text.Json.JsonSerializer.Serialize(new
    {
        l.Name, l.IsActive, l.IsInitial, l.TriggerType, l.TriggerValue, l.ActionDescription, l.RoutingStrategy, l.TargetUserId,
        l.FallbackPolicy, l.AssignOnAuto, l.ManualAllowed, l.TriggerPriorities,
        Roles = (l.Roles.Count > 0 ? l.Roles.Select(r => r.RoleKey) : [l.TargetRole]).OrderBy(k => k).ToList(),
    });

    private static void Apply(EscalationLevelConfig target, EscalationLevelConfigDto input)
    {
        target.Name = string.IsNullOrWhiteSpace(input.Name) ? $"Level {input.LevelNumber}" : input.Name.Trim();
        target.AssignmentType = EscalationService.AssignmentRole;
        var roles = RolesOf(input);
        target.Roles = roles.Select(r => new EscalationLevelRole { LevelId = target.Id, RoleKey = (r.Key ?? r.Name ?? "").Trim(), RoleName = (r.Name ?? r.Key ?? "").Trim() }).ToList();
        target.TargetRole = string.Join(", ", target.Roles.Select(r => r.RoleName));
        target.IsInitial = input.IsInitial;
        var trigger = input.IsInitial ? null : EscalationTriggers.Canonical(input.TriggerType);
        target.TriggerType = trigger ?? (input.IsInitial ? EscalationTriggers.ManualOnly : input.TriggerType ?? string.Empty);
        target.TriggerValue = trigger != null && EscalationTriggers.Find(trigger)!.NeedsValue ? input.TriggerValue : null;
        target.TriggerDescription = input.IsInitial ? "Initial handlers" : EscalationTriggers.Describe(target.TriggerType, target.TriggerValue);
        target.ActionDescription = input.ActionDescription?.Trim() ?? string.Empty;
        target.RoutingStrategy = EscalationRouting.Canonical(input.RoutingStrategy) ?? input.RoutingStrategy ?? EscalationRouting.Team;
        target.TargetUserId = target.RoutingStrategy == EscalationRouting.SpecificUser ? input.TargetUserId : null;
        target.FallbackPolicy = EscalationFallbacks.Canonical(input.FallbackPolicy) ?? input.FallbackPolicy ?? EscalationFallbacks.NextLevel;
        target.AssignOnAuto = input.AssignOnAuto;
        target.ManualAllowed = input.ManualAllowed;
        var priorities = input.TriggerPriorities.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        target.TriggerPriorities = priorities.Count == 0 ? null : string.Join(",", priorities);
        // No longer configured: who is told is who the case is escalated to, and the urgency never reached the Host.
        target.ReassignOwner = false;
        target.NotifyRoleHolders = false;
        target.NotifyCaseOwner = false;
        target.NotifyTeamLead = false;
        target.NotificationPriority = "High";
        target.DisplayOrder = input.LevelNumber;
        target.IsActive = input.IsActive;
    }

    private void InvalidatePolicy() => _cache?.InvalidateAll();

    /// <summary>Keeps every case on a level that exists after the matrix changed.</summary>
    private async Task ClampCaseLevelsAsync(CancellationToken ct)
    {
        var max = await _context.EscalationLevelConfigs.Select(l => (int?)l.LevelNumber).MaxAsync(ct) ?? 0;
        await _context.Cases.Where(c => c.EscalationLevel > max).ExecuteUpdateAsync(u => u.SetProperty(c => c.EscalationLevel, max), ct);
    }

    public async Task<SlaRoutingConfigResponseDto> GetFullConfigurationAsync(CancellationToken ct = default)
    {
        // Six independent reads and the Host's role list, side by side: each read is one round trip to the database, and the
        // page used to wait for all of them one after another (about 3 s against the hosted database).
        List<PrioritySlaRule> rules = new();
        List<CategoryOptionDto> categories = new();
        List<BusinessHour> businessHoursRaw = new();
        List<PublicHolidayDto> holidays = new();
        List<EscalationLevelConfig> levelRows = new();
        string timeZoneId = string.Empty;
        (int Value, string Unit) ticketDefault = (Tickets.TicketSlaTargets.FallbackMinutes, "Minutes");
        List<string> roles = new();
        List<HostRoleRef> roleRefs = new();
        string? rolesUnavailable = null;

        // 6. Roles a level may be given: the Host roles granted Case Management access (read live from the Host). Without a
        // context factory (tests) the directory may read the request's context, so it then runs after the reads, not beside them.
        async Task ReadRoles()
        {
            if (_directory == null) return;
            try
            {
                roleRefs = (await _directory.CaseRoleRefsAsync(ct)).ToList();
                roles = roleRefs.Select(r => r.Name).ToList();
            }
            catch (HostUsersException ex) { rolesUnavailable = ex.Message; }
        }
        var rolesTask = _contexts != null ? ReadRoles() : Task.CompletedTask;

        await ParallelReads.RunAsync(_contexts, _context, ct,
            // 1. Priority SLA Rules & Mappings (ordered by the administrator-defined display order)
            async db => rules = await db.PrioritySlaRules.Include(r => r.CategoryMappings).AsNoTracking()
                .OrderBy(r => r.DisplayOrder).ThenBy(r => r.Priority).ToListAsync(ct),
            // 2. Sub-categories a priority can be applied to: every active one, plus any that is already mapped (so a mapping
            //    never silently disappears from the screen). Grouped by the business department the sub-category belongs to.
            async db => categories = await db.DepartmentSubCategories.AsNoTracking()
                .Where(s => s.IsActive || db.PriorityCategoryMappings.Any(m => m.DepartmentSubCategoryId == s.Id))
                .OrderBy(s => s.Department!.Name).ThenBy(s => s.Name)
                .Select(s => new CategoryOptionDto
                {
                    Id = s.Id,
                    Name = s.Name,
                    DepartmentId = s.DepartmentId ?? Guid.Empty,
                    DepartmentName = s.Department != null ? s.Department.Name : "No department",
                    IsActive = s.IsActive
                })
                .ToListAsync(ct),
            // 3. Business Hours (7 Days)
            async db => businessHoursRaw = await db.BusinessHours.AsNoTracking().ToListAsync(ct),
            // 4. Public Holidays
            async db => holidays = await db.PublicHolidays.AsNoTracking().OrderBy(h => h.HolidayDate)
                .Select(h => new PublicHolidayDto { Id = h.Id, HolidayDate = h.HolidayDate, Name = h.Name, IsActive = h.IsActive })
                .ToListAsync(ct),
            // 5. Escalation Levels
            async db => levelRows = await db.EscalationLevelConfigs.AsNoTracking().Include(l => l.Roles).OrderBy(l => l.LevelNumber).ToListAsync(ct),
            async db =>
            {
                var setting = await db.BusinessCalendarSettings.AsNoTracking()
                    .Select(t => new { t.TimeZoneId, t.TicketDefaultAssignValue, t.TicketDefaultAssignUnit }).FirstOrDefaultAsync(ct);
                timeZoneId = setting?.TimeZoneId ?? string.Empty;
                if (setting != null) ticketDefault = (setting.TicketDefaultAssignValue, setting.TicketDefaultAssignUnit);
            });
        await rolesTask;
        if (_contexts == null) await ReadRoles();

        // The time zone is the Host's (Settings → Manage Time Zone); the stored one is shown only when the Host cannot be read.
        var hostZone = _hostZone == null ? null : await _hostZone.GetAsync(ct);
        if (hostZone != null) timeZoneId = hostZone;

        var ruleDtos = rules.Select(r => new PrioritySlaRuleDto
        {
            Id = r.Id,
            Priority = r.Priority,
            DisplayOrder = r.DisplayOrder,
            IsActive = r.IsActive,
            FirstResponseValue = r.FirstResponseValue,
            FirstResponseUnit = r.FirstResponseUnit,
            FirstResponseMinutes = r.FirstResponseMinutes,
            InternalResolutionValue = r.InternalResolutionValue,
            InternalResolutionUnit = r.InternalResolutionUnit,
            InternalResolutionMinutes = r.InternalResolutionMinutes,
            ExternalResolutionValue = r.ExternalResolutionValue,
            ExternalResolutionUnit = r.ExternalResolutionUnit,
            ExternalResolutionMinutes = r.ExternalResolutionMinutes,
            TicketAssignValue = r.TicketAssignValue,
            TicketAssignUnit = r.TicketAssignUnit,
            TicketAssignMinutes = r.TicketAssignMinutes,
            AppliedSubCategoryIds = r.CategoryMappings.Select(m => m.DepartmentSubCategoryId).ToList()
        }).ToList();

        var businessHours = businessHoursRaw
            .OrderBy(b => b.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)b.DayOfWeek) // Monday first, Sunday last
            .Select(b => new BusinessHourDto
            {
                Id = b.Id,
                DayOfWeek = (int)b.DayOfWeek,
                DayName = b.DayName,
                IsEnabled = b.IsEnabled,
                StartTime = b.StartTime.ToString(@"hh\:mm"),
                EndTime = b.EndTime.ToString(@"hh\:mm")
            })
            .ToList();

        var levels = levelRows.Select(l =>
        {
            var dto = ToDto(l);
            if (rolesUnavailable == null)
            {
                Canonicalise([dto], roleRefs);   // the Host's current names
                dto.RoleUnavailable = dto.Roles.Any(r => !roleRefs.Any(h => h.Key == r.Key));
            }
            return dto;
        }).ToList();
        var users = await _context.Users.AsNoTracking().Where(u => u.IsActive && u.ExternalUserId != null)
            .OrderBy(u => u.Name).Select(u => new UserOptionDto { Id = u.Id, Name = u.Name, Role = u.Role }).ToListAsync(ct);

        return new SlaRoutingConfigResponseDto
        {
            PriorityRules = ruleDtos,
            AvailableCategories = categories,
            BusinessHours = businessHours,
            PublicHolidays = holidays,
            EscalationLevels = levels,
            AvailableRoles = roles,
            AvailableRoleRefs = roleRefs.Select(r => new EscalationRoleDto { Key = r.Key, Name = r.Name }).ToList(),
            RoutingStrategies = EscalationRouting.All.ToList(),
            FallbackPolicies = EscalationFallbacks.All.ToList(),
            AvailableUsers = users,
            RolesUnavailableMessage = rolesUnavailable,
            NotificationPriorities = EscalationNotificationPriorities.All.ToList(),
            TimeZoneId = timeZoneId,
            TimeZoneFromHost = hostZone != null,
            TicketDefaultAssignValue = ticketDefault.Value,
            TicketDefaultAssignUnit = ticketDefault.Unit,
            AvailableTimeZones = TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Where(id => id.Contains('/')).OrderBy(id => id).ToList(),
            TriggerTypes = EscalationTriggers.All.Select(t => new EscalationTriggerOptionDto
            {
                Type = t.Type, Label = t.Label, NeedsValue = t.NeedsValue, Unit = t.Unit, Min = t.Min, Max = t.Max,
            }).ToList(),
            AssignmentTypes = new List<string> { EscalationService.AssignmentRole },
        };
    }

    public async Task<bool> UpdateConfigurationAsync(UpdateSlaRoutingConfigRequestDto request, Guid actingUserId, CancellationToken ct = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await _context.Database.BeginTransactionAsync(ct);
            try
            {
                var now = DateTime.UtcNow;

                // 1. Update Priority SLA Rules & sub-category mappings
                var existingRules = await _context.PrioritySlaRules.ToListAsync(ct);
                var validUnits = new[] { "Minutes", "Hours" };

                foreach (var input in request.PriorityRules)
                {
                    if (string.IsNullOrWhiteSpace(input.Priority))
                        throw new InvalidOperationException("Every priority needs a name.");
                    foreach (var (label, value, unit) in new[]
                    {
                        ("First response", input.FirstResponseValue, input.FirstResponseUnit),
                        ("Internal resolution", input.InternalResolutionValue, input.InternalResolutionUnit),
                        ("External resolution", input.ExternalResolutionValue, input.ExternalResolutionUnit),
                    })
                    {
                        if (value < 1)
                            throw new InvalidOperationException($"{label} for '{input.Priority}' must be at least 1.");
                        if (!validUnits.Contains(unit, StringComparer.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"{label} unit for '{input.Priority}' must be Minutes or Hours.");
                    }
                    if (input.TicketAssignValue != null && !Validators.TicketAssignLimits.IsValid(input.TicketAssignValue, input.TicketAssignUnit))
                        throw new InvalidOperationException($"{Validators.TicketAssignLimits.Message($"Assign ticket within for '{input.Priority}'")}");
                }
                if (request.TicketDefaultAssignValue != null && !Validators.TicketAssignLimits.IsValid(request.TicketDefaultAssignValue, request.TicketDefaultAssignUnit))
                    throw new InvalidOperationException(Validators.TicketAssignLimits.Message("Tickets without a priority"));

                // Names are unique (case-insensitive), and at least one priority stays active for new cases.
                var duplicateNames = request.PriorityRules
                    .GroupBy(r => r.Priority.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (duplicateNames.Count > 0)
                    throw new InvalidOperationException($"Each priority needs a different name. Used more than once: {string.Join(", ", duplicateNames)}.");
                var newNames = request.PriorityRules.Where(r => r.Id == Guid.Empty).Select(r => r.Priority.Trim()).ToList();
                var clash = existingRules.FirstOrDefault(e => newNames.Contains(e.Priority, StringComparer.OrdinalIgnoreCase));
                if (clash != null)
                    throw new InvalidOperationException($"A priority named '{clash.Priority}' already exists.");
                var activeAfter = existingRules.Count(e => request.PriorityRules.FirstOrDefault(r => r.Id == e.Id)?.IsActive ?? e.IsActive)
                                  + request.PriorityRules.Count(r => r.Id == Guid.Empty && r.IsActive);
                if (activeAfter == 0)
                    throw new InvalidOperationException("At least one priority must stay active.");

                // One priority per sub-category, and every referenced sub-category must exist.
                var allIds = request.PriorityRules.SelectMany(r => r.AppliedSubCategoryIds ?? new()).ToList();
                var duplicated = allIds.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (duplicated.Count > 0)
                {
                    var names = await _context.DepartmentSubCategories
                        .Where(s => duplicated.Contains(s.Id))
                        .Select(s => (s.Department != null ? s.Department.Name : "No department") + " / " + s.Name)
                        .ToListAsync(ct);
                    throw new InvalidOperationException($"A sub-category can have only one priority. Assigned more than once: {string.Join(", ", names)}.");
                }
                var knownIds = (await _context.DepartmentSubCategories
                    .Where(s => allIds.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
                var unknown = allIds.Where(id => !knownIds.Contains(id)).ToList();
                if (unknown.Count > 0)
                    throw new InvalidOperationException("One or more sub-categories no longer exist. Reload the page and try again.");

                // Rebuild the mappings cleanly.
                var allMappings = await _context.PriorityCategoryMappings.ToListAsync(ct);
                _context.PriorityCategoryMappings.RemoveRange(allMappings);
                await _context.SaveChangesAsync(ct);

                foreach (var inputRule in request.PriorityRules)
                {
                    var rule = (inputRule.Id != Guid.Empty ? existingRules.FirstOrDefault(r => r.Id == inputRule.Id) : null)
                               ?? existingRules.FirstOrDefault(r => r.Priority.Equals(inputRule.Priority, StringComparison.OrdinalIgnoreCase));

                    int Minutes(int value, string unit) => unit.Equals("Hours", StringComparison.OrdinalIgnoreCase) ? value * 60 : value;
                    int frMinutes = Minutes(inputRule.FirstResponseValue, inputRule.FirstResponseUnit);
                    int intMinutes = Minutes(inputRule.InternalResolutionValue, inputRule.InternalResolutionUnit);
                    int extMinutes = Minutes(inputRule.ExternalResolutionValue, inputRule.ExternalResolutionUnit);

                    if (rule == null)
                    {
                        rule = new PrioritySlaRule
                        {
                            Id = Guid.NewGuid(),
                            Priority = inputRule.Priority.Trim(),
                            DisplayOrder = existingRules.Count == 0 ? 1 : existingRules.Max(r => r.DisplayOrder) + 1,
                            IsActive = inputRule.IsActive,
                            Version = 1,
                            CreatedAt = now
                        };
                        existingRules.Add(rule);
                        _context.PrioritySlaRules.Add(rule);
                    }
                    else
                    {
                        // The SLA version is what cases snapshot, so it only moves when a target really changed.
                        var changed = rule.FirstResponseMinutes != frMinutes
                                      || rule.InternalResolutionMinutes != intMinutes
                                      || rule.ExternalResolutionMinutes != extMinutes;
                        if (changed) { rule.Version += 1; rule.UpdatedAt = now; }
                        // Inactive = not offered for new cases; cases that already use it keep it. (Renaming is a separate action:
                        // it must follow the name into cases and routing rules — see ConfigurableSettingsService.UpdateSeverityAsync.)
                        if (rule.IsActive != inputRule.IsActive) { rule.IsActive = inputRule.IsActive; rule.UpdatedAt = now; }
                    }

                    rule.FirstResponseValue = inputRule.FirstResponseValue;
                    rule.FirstResponseUnit = inputRule.FirstResponseUnit;
                    rule.FirstResponseMinutes = frMinutes;
                    rule.InternalResolutionValue = inputRule.InternalResolutionValue;
                    rule.InternalResolutionUnit = inputRule.InternalResolutionUnit;
                    rule.InternalResolutionMinutes = intMinutes;
                    rule.ExternalResolutionValue = inputRule.ExternalResolutionValue;
                    rule.ExternalResolutionUnit = inputRule.ExternalResolutionUnit;
                    rule.ExternalResolutionMinutes = extMinutes;

                    // Ticket SLA: only when sent (a screen that does not know the field leaves it as it is). It is not part of the
                    // case's snapshot, so it never moves the rule's Version.
                    if (inputRule.TicketAssignValue is { } ticketValue)
                    {
                        var ticketUnit = CanonicalUnit(inputRule.TicketAssignUnit!);
                        var ticketMinutes = Validators.TicketAssignLimits.Minutes(ticketValue, ticketUnit);
                        if (rule.TicketAssignMinutes != ticketMinutes || rule.TicketAssignUnit != ticketUnit) rule.UpdatedAt = now;
                        rule.TicketAssignValue = ticketValue;
                        rule.TicketAssignUnit = ticketUnit;
                        rule.TicketAssignMinutes = ticketMinutes;
                    }

                    foreach (var subCategoryId in (inputRule.AppliedSubCategoryIds ?? new()).Distinct())
                    {
                        _context.PriorityCategoryMappings.Add(new PriorityCategoryMapping
                        {
                            Id = Guid.NewGuid(),
                            PrioritySlaRuleId = rule.Id,
                            DepartmentSubCategoryId = subCategoryId,
                            CreatedAt = now
                        });
                    }
                }

                // 1b. Time zone of the working calendar: the Host's, never chosen here. The stored copy is only the fallback for
                //     when the Host cannot be read, so a save refreshes it to the Host's zone. Without a Host (standalone) the
                //     request's zone is kept, as before.
                var zoneToStore = (_hostZone == null ? null : await _hostZone.GetAsync(ct))
                                  ?? (_hostZone == null ? request.TimeZoneId?.Trim() : null);
                if (!string.IsNullOrWhiteSpace(zoneToStore))
                {
                    BusinessTimeService.FindTimeZone(zoneToStore);   // throws a clear error for an unknown zone
                    var setting = await _context.BusinessCalendarSettings.FirstOrDefaultAsync(ct);
                    if (setting == null)
                        _context.BusinessCalendarSettings.Add(new BusinessCalendarSetting { Id = Guid.NewGuid(), TimeZoneId = zoneToStore, CreatedAt = now });
                    else if (setting.TimeZoneId != zoneToStore)
                    {
                        setting.TimeZoneId = zoneToStore;
                        setting.UpdatedAt = now;
                    }
                }

                // 1c. Assign-within time of tickets without a priority (same row as the time zone).
                if (request.TicketDefaultAssignValue is { } defaultValue)
                {
                    var unit = CanonicalUnit(request.TicketDefaultAssignUnit!);
                    var setting = _context.BusinessCalendarSettings.Local.FirstOrDefault()
                                  ?? await _context.BusinessCalendarSettings.FirstOrDefaultAsync(ct);
                    if (setting == null)
                    {
                        setting = new BusinessCalendarSetting { Id = Guid.NewGuid(), CreatedAt = now };
                        _context.BusinessCalendarSettings.Add(setting);
                    }
                    setting.TicketDefaultAssignValue = defaultValue;
                    setting.TicketDefaultAssignUnit = unit;
                    setting.TicketDefaultAssignMinutes = Validators.TicketAssignLimits.Minutes(defaultValue, unit);
                    setting.UpdatedAt = now;
                }

                // 2. Update Business Hours
                var existingHours = await _context.BusinessHours.ToListAsync(ct);
                foreach (var inputBh in request.BusinessHours)
                {
                    var bh = existingHours.FirstOrDefault(b => (int)b.DayOfWeek == inputBh.DayOfWeek);
                    TimeSpan.TryParse(inputBh.StartTime, out var sTime);
                    TimeSpan.TryParse(inputBh.EndTime, out var eTime);

                    if (inputBh.IsEnabled && sTime > eTime)
                    {
                        throw new InvalidOperationException($"Invalid business hours for {inputBh.DayName}: opening time ({inputBh.StartTime}) cannot be after closing time ({inputBh.EndTime}).");
                    }

                    if (bh == null)
                    {
                        _context.BusinessHours.Add(new BusinessHour
                        {
                            Id = Guid.NewGuid(),
                            DayOfWeek = (DayOfWeek)inputBh.DayOfWeek,
                            DayName = ((DayOfWeek)inputBh.DayOfWeek).ToString(),
                            IsEnabled = inputBh.IsEnabled,
                            StartTime = sTime,
                            EndTime = eTime,
                            CreatedAt = now
                        });
                    }
                    else
                    {
                        bh.IsEnabled = inputBh.IsEnabled;
                        bh.StartTime = sTime;
                        bh.EndTime = eTime;
                        bh.UpdatedAt = now;
                    }
                }

                // 3. Update Escalation Level Configs (the whole matrix is checked first; an incomplete level is never stored)
                // A save from SLA Maintenance carries the escalation matrix back exactly as it was read: then it is left alone
                // (not checked, not rewritten), so a role the Host has since revoked does not block fixing an SLA. Any change
                // to the matrix is checked in full, roles included.
                var inputLevels = request.EscalationLevels.OrderBy(l => l.LevelNumber).ToList();
                var existingLevels = await _context.EscalationLevelConfigs.Include(l => l.Roles).ToListAsync(ct);
                var hostRoles = await HostRolesAsync(ct);
                Canonicalise(inputLevels, hostRoles);
                if (!SameMatrix(existingLevels, inputLevels))
                {
                    await ValidateMatrixAsync(inputLevels, hostRoles, ct);
                    var removedNumbers = existingLevels.Select(l => l.LevelNumber).Except(inputLevels.Select(l => l.LevelNumber)).ToList();
                    await RefuseRemovingOccupiedLevelsAsync(removedNumbers, ct);

                    _context.EscalationLevelConfigs.RemoveRange(existingLevels);
                    await _context.SaveChangesAsync(ct);

                    foreach (var inputLvl in inputLevels)
                    {
                        var level = new EscalationLevelConfig { Id = Guid.NewGuid(), LevelNumber = inputLvl.LevelNumber, CreatedAt = now };
                        Apply(level, inputLvl);
                        _context.EscalationLevelConfigs.Add(level);
                    }
                    await _context.SaveChangesAsync(ct);
                    await ClampCaseLevelsAsync(ct);
                }

                // 4. Record Audit Log in CaseEvents
                _context.CaseEvents.Add(new CaseEvent
                {
                    Id = Guid.NewGuid(),
                    CaseId = null,
                    EventType = EventType.Other,
                    Module = "SlaEscalation",
                    EntityName = "SLA Maintenance Configuration",
                    ActionType = "UPDATE_CONFIG",
                    Message = "Administrator saved updated Cases SLA Matrix, Business Hours, and Escalation Matrix.",
                    UserId = actingUserId,
                    CreatedAt = now,
                    IsInternal = true
                });

                await _context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                InvalidatePolicy();
                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(ct);
                _logger.LogError(ex, "Failed to save SLA Maintenance configuration atomically.");
                throw;
            }
        });
    }

    public async Task<PublicHolidayDto> AddPublicHolidayAsync(CreatePublicHolidayDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        var dateUtc = DateTime.SpecifyKind(dto.HolidayDate.Date, DateTimeKind.Utc);
        var existing = await _context.PublicHolidays.FirstOrDefaultAsync(h => h.HolidayDate == dateUtc, ct);
        if (existing != null)
        {
            throw new InvalidOperationException($"A public holiday is already configured for {dateUtc:yyyy-MM-dd}.");
        }

        var holiday = new PublicHoliday
        {
            Id = Guid.NewGuid(),
            HolidayDate = dateUtc,
            Name = dto.Name.Trim(),
            IsActive = dto.IsActive,
            CreatedAt = DateTime.UtcNow
        };

        _context.PublicHolidays.Add(holiday);
        _context.CaseEvents.Add(new CaseEvent
        {
            Id = Guid.NewGuid(),
            CaseId = null,
            EventType = EventType.Other,
            Module = "SlaEscalation",
            EntityName = "Public Holiday",
            ActionType = "CREATE",
            Message = $"Added public holiday '{holiday.Name}' ({dateUtc:yyyy-MM-dd}).",
            UserId = actingUserId,
            CreatedAt = DateTime.UtcNow,
            IsInternal = true
        });

        await _context.SaveChangesAsync(ct);

        return new PublicHolidayDto
        {
            Id = holiday.Id,
            HolidayDate = holiday.HolidayDate,
            Name = holiday.Name,
            IsActive = holiday.IsActive
        };
    }

    public async Task<PublicHolidayDto?> UpdatePublicHolidayAsync(Guid id, UpdatePublicHolidayDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        var holiday = await _context.PublicHolidays.FindAsync(new object[] { id }, ct);
        if (holiday == null) return null;

        var dateUtc = DateTime.SpecifyKind(dto.HolidayDate.Date, DateTimeKind.Utc);
        var conflict = await _context.PublicHolidays.FirstOrDefaultAsync(h => h.Id != id && h.HolidayDate == dateUtc, ct);
        if (conflict != null)
        {
            throw new InvalidOperationException($"Another public holiday is already configured for {dateUtc:yyyy-MM-dd}.");
        }

        holiday.HolidayDate = dateUtc;
        holiday.Name = dto.Name.Trim();
        holiday.IsActive = dto.IsActive;
        holiday.UpdatedAt = DateTime.UtcNow;

        _context.CaseEvents.Add(new CaseEvent
        {
            Id = Guid.NewGuid(),
            CaseId = null,
            EventType = EventType.Other,
            Module = "SlaEscalation",
            EntityName = "Public Holiday",
            ActionType = "UPDATE",
            Message = $"Updated public holiday '{holiday.Name}' ({dateUtc:yyyy-MM-dd}).",
            UserId = actingUserId,
            CreatedAt = DateTime.UtcNow,
            IsInternal = true
        });

        await _context.SaveChangesAsync(ct);

        return new PublicHolidayDto
        {
            Id = holiday.Id,
            HolidayDate = holiday.HolidayDate,
            Name = holiday.Name,
            IsActive = holiday.IsActive
        };
    }

    public async Task<bool> DeletePublicHolidayAsync(Guid id, Guid actingUserId, CancellationToken ct = default)
    {
        var holiday = await _context.PublicHolidays.FindAsync(new object[] { id }, ct);
        if (holiday == null) return false;

        _context.PublicHolidays.Remove(holiday);
        _context.CaseEvents.Add(new CaseEvent
        {
            Id = Guid.NewGuid(),
            CaseId = null,
            EventType = EventType.Other,
            Module = "SlaEscalation",
            EntityName = "Public Holiday",
            ActionType = "DELETE",
            Message = $"Deleted public holiday '{holiday.Name}' ({holiday.HolidayDate:yyyy-MM-dd}).",
            UserId = actingUserId,
            CreatedAt = DateTime.UtcNow,
            IsInternal = true
        });

        await _context.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>"Hours" or "Minutes", as stored (the request may send any casing).</summary>
    private static string CanonicalUnit(string unit) => unit.Equals("Hours", StringComparison.OrdinalIgnoreCase) ? "Hours" : "Minutes";

    private static EscalationLevelConfigDto ToDto(EscalationLevelConfig l) => new()
    {
        Id = l.Id,
        LevelNumber = l.LevelNumber,
        Name = l.Name,
        AssignmentType = EscalationService.AssignmentRole,
        TargetRole = string.Join(", ", EscalationLevelRules.RoleNames(l)),
        Roles = l.Roles.Count > 0
            ? l.Roles.Select(r => new EscalationRoleDto { Key = r.RoleKey, Name = r.RoleName }).ToList()
            : EscalationLevelRules.RoleNames(l).Select(n => new EscalationRoleDto { Key = n, Name = n }).ToList(),
        IsInitial = l.IsInitial,
        TriggerType = l.IsInitial ? string.Empty : EscalationTriggers.Canonical(l.TriggerType) ?? l.TriggerType,
        TriggerValue = l.TriggerValue,
        TriggerDescription = l.IsInitial ? "Initial handlers" : EscalationTriggers.Describe(l.TriggerType, l.TriggerValue),
        ActionDescription = l.ActionDescription,
        RoutingStrategy = l.RoutingStrategy,
        TargetUserId = l.TargetUserId,
        FallbackPolicy = l.FallbackPolicy,
        AssignOnAuto = l.AssignOnAuto,
        ManualAllowed = l.ManualAllowed,
        TriggerPriorities = string.IsNullOrWhiteSpace(l.TriggerPriorities) ? new() : l.TriggerPriorities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        IsActive = l.IsActive,
        MissingFields = EscalationLevelRules.MissingFields(l).ToList(),
    };

    private void AuditLevel(string action, string message, Guid actingUserId) =>
        _context.CaseEvents.Add(new CaseEvent
        {
            Id = Guid.NewGuid(),
            CaseId = null,
            EventType = EventType.Other,
            Module = "SlaEscalation",
            EntityName = "Escalation Level",
            ActionType = action,
            Message = message,
            UserId = actingUserId,
            CreatedAt = DateTime.UtcNow,
            IsInternal = true
        });

    public async Task<EscalationLevelConfigDto> AddEscalationLevelAsync(CreateEscalationLevelDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        var existing = await _context.EscalationLevelConfigs.AsNoTracking().Include(l => l.Roles).OrderBy(l => l.LevelNumber).ToListAsync(ct);
        int levelNumber = dto.LevelNumber.HasValue && dto.LevelNumber.Value > 0
            ? dto.LevelNumber.Value
            : (existing.Count > 0 ? existing.Max(l => l.LevelNumber) + 1 : 1);
        if (existing.Any(l => l.LevelNumber == levelNumber))
            throw new InvalidOperationException($"Level {levelNumber} already exists.");

        var input = new EscalationLevelConfigDto
        {
            LevelNumber = levelNumber, Name = dto.Name ?? string.Empty, TargetRole = dto.TargetRole, TriggerType = dto.TriggerType,
            TriggerValue = dto.TriggerValue, ActionDescription = dto.ActionDescription ?? string.Empty, IsActive = true,
            FallbackPolicy = EscalationFallbacks.Hold,
        };
        var all = existing.Select(ToDto).Append(input).ToList();
        var host = await HostRolesAsync(ct);
        Canonicalise(all, host);
        await ValidateMatrixAsync(all, host, ct);

        var level = new EscalationLevelConfig { Id = Guid.NewGuid(), LevelNumber = levelNumber, CreatedAt = DateTime.UtcNow };
        Apply(level, input);
        _context.EscalationLevelConfigs.Add(level);
        AuditLevel("CREATE", $"Created escalation level '{level.Name}' (role: {level.TargetRole}, trigger: {level.TriggerDescription}).", actingUserId);
        await _context.SaveChangesAsync(ct);
        InvalidatePolicy();

        return ToDto(level);
    }

    public async Task<EscalationLevelConfigDto?> UpdateEscalationLevelAsync(Guid id, UpdateEscalationLevelDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        var level = await _context.EscalationLevelConfigs.Include(l => l.Roles).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (level == null) return null;

        var input = ToDto(level);
        if (!string.IsNullOrWhiteSpace(dto.Name)) input.Name = dto.Name;
        if (dto.TargetRole != null) { input.TargetRole = dto.TargetRole; input.Roles = new(); }
        if (dto.TriggerType != null) input.TriggerType = dto.TriggerType;
        if (dto.TriggerValue.HasValue) input.TriggerValue = dto.TriggerValue;
        if (dto.ActionDescription != null) input.ActionDescription = dto.ActionDescription;
        if (dto.IsActive.HasValue) input.IsActive = dto.IsActive.Value;

        var others = await _context.EscalationLevelConfigs.AsNoTracking().Include(l => l.Roles).Where(l => l.Id != id).ToListAsync(ct);
        var all = others.Select(ToDto).Append(input).ToList();
        var host = await HostRolesAsync(ct);
        Canonicalise(all, host);
        await ValidateMatrixAsync(all, host, ct);

        _context.EscalationLevelRoles.RemoveRange(level.Roles);
        await _context.SaveChangesAsync(ct);
        Apply(level, input);
        level.UpdatedAt = DateTime.UtcNow;
        AuditLevel("UPDATE", $"Updated escalation level '{level.Name}' (role: {level.TargetRole}, trigger: {level.TriggerDescription}, {(level.IsActive ? "active" : "inactive")}).", actingUserId);
        await _context.SaveChangesAsync(ct);
        InvalidatePolicy();

        return ToDto(level);
    }

    /// <summary>
    /// Deletes a level and renumbers the rest. Refused while open cases sit at the level or above it: moving them would rewrite
    /// where they stand in the hierarchy. (The page now deletes levels by saving the whole matrix; this endpoint stays for API clients.)
    /// </summary>
    public async Task<bool> DeleteEscalationLevelAsync(Guid id, Guid actingUserId, CancellationToken ct = default)
    {
        var level = await _context.EscalationLevelConfigs.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (level == null) return false;

        var removedNumber = level.LevelNumber;
        await RefuseRemovingOccupiedLevelsAsync([removedNumber], ct);
        _context.EscalationLevelConfigs.Remove(level);
        await _context.SaveChangesAsync(ct);

        // Re-sequence the remaining levels to 1, 2, 3… (via negative numbers, because LevelNumber is unique).
        var remaining = await _context.EscalationLevelConfigs.OrderBy(l => l.LevelNumber).ToListAsync(ct);
        for (int i = 0; i < remaining.Count; i++) remaining[i].LevelNumber = -(i + 1);
        await _context.SaveChangesAsync(ct);

        for (int i = 0; i < remaining.Count; i++)
        {
            int newNum = i + 1;
            remaining[i].LevelNumber = newNum;
            remaining[i].DisplayOrder = newNum;
            if (remaining[i].Name.StartsWith("Level ")) remaining[i].Name = $"Level {newNum}";
            remaining[i].UpdatedAt = DateTime.UtcNow;
        }
        AuditLevel("DELETE", $"Deleted escalation level '{level.Name}' and re-sequenced remaining levels.", actingUserId);
        await _context.SaveChangesAsync(ct);
        await ClampCaseLevelsAsync(ct);

        InvalidatePolicy();
        return true;
    }

    private static readonly CaseStatus[] FinishedStatuses = [CaseStatus.Resolved, CaseStatus.Closed, CaseStatus.Cancelled];

    /// <summary>Refuses a matrix change that would remove (or renumber away) a level open cases have reached.</summary>
    private async Task RefuseRemovingOccupiedLevelsAsync(IReadOnlyCollection<int> removed, CancellationToken ct)
    {
        if (removed.Count == 0) return;
        var lowest = removed.Min();
        var occupied = await _context.Cases.AsNoTracking()
            .Where(c => c.EscalationLevel >= lowest && !FinishedStatuses.Contains(c.Status))
            .GroupBy(c => c.EscalationLevel).Select(g => new { Level = g.Key, Count = g.Count() }).OrderBy(x => x.Level).ToListAsync(ct);
        if (occupied.Count == 0) return;
        throw new FieldValidationException([new FieldError("escalationLevels",
            $"Open cases have reached {string.Join(", ", occupied.Select(o => $"Level {o.Level} ({o.Count})"))}. Removing Level {lowest} would move them. "
            + "Resolve or de-escalate those cases first, or add levels at the top instead.")]);
    }

    /// <summary>
    /// Finds the active sub-category (and the priority configured for it) a new case would belong to.
    /// Both the match and the mapping are by ID — names only locate the sub-category within its department.
    /// <paramref name="departmentId"/> is the case's department, which owns its sub-categories.
    /// </summary>
    private async Task<(Guid SubCategoryId, PrioritySlaRule? Rule)?> FindMappedRuleAsync(Guid? departmentId, string? subCategoryName, CancellationToken ct)
    {
        if (!departmentId.HasValue || string.IsNullOrWhiteSpace(subCategoryName)) return null;

        var lowered = subCategoryName.Trim().ToLower();
        var subCategoryId = await _context.DepartmentSubCategories
            .AsNoTracking()
            .Where(s => s.DepartmentId == departmentId.Value && s.IsActive && s.Name.ToLower() == lowered)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
        if (subCategoryId == null) return null;

        var rule = await _context.PriorityCategoryMappings
            .AsNoTracking()
            .Where(m => m.DepartmentSubCategoryId == subCategoryId.Value)
            .Select(m => m.PrioritySlaRule)
            .FirstOrDefaultAsync(ct);

        return (subCategoryId.Value, rule);
    }

    public async Task<PriorityResolution> ResolveEffectivePriorityAsync(
        Guid? departmentId, string? subCategoryName, string? requestedPriority, CancellationToken ct = default)
    {
        // 1. The sub-category's configured priority is authoritative.
        var mapped = await FindMappedRuleAsync(departmentId, subCategoryName, ct);
        if (mapped?.Rule is { IsActive: true } mappedRule)
            return new PriorityResolution(mappedRule.Priority, "SubCategoryMapping", mapped.Value.SubCategoryId);

        // 2. Otherwise the requested priority, which must be one of the configured, active ones.
        if (!string.IsNullOrWhiteSpace(requestedPriority))
        {
            var lowered = requestedPriority.Trim().ToLower();
            var rule = await _context.PrioritySlaRules.AsNoTracking()
                .Where(r => r.IsActive && r.Priority.ToLower() == lowered)
                .FirstOrDefaultAsync(ct);
            if (rule != null)
                return new PriorityResolution(rule.Priority, "Requested", mapped?.SubCategoryId);

            var available = await _context.PrioritySlaRules.AsNoTracking()
                .Where(r => r.IsActive).OrderBy(r => r.DisplayOrder).Select(r => r.Priority).ToListAsync(ct);
            throw new InvalidOperationException(
                $"'{requestedPriority.Trim()}' is not a configured priority. Configured priorities: {string.Join(", ", available)}.");
        }

        // 3. Never guess a default: an unmapped sub-category with no choice made is an input error.
        throw new InvalidOperationException(
            "A priority is required: this sub-category has no configured priority, so one must be selected.");
    }

    public async Task<PriorityResolutionDto> PreviewPriorityAsync(Guid? departmentId, string? subCategoryName, CancellationToken ct = default)
    {
        var mapped = await FindMappedRuleAsync(departmentId, subCategoryName, ct);
        if (mapped?.Rule is not { IsActive: true } rule)
            return new PriorityResolutionDto { IsMapped = false, SubCategoryId = mapped?.SubCategoryId };

        return new PriorityResolutionDto
        {
            IsMapped = true,
            Priority = rule.Priority,
            SubCategoryId = mapped!.Value.SubCategoryId,
            InternalHours = (int)Math.Ceiling(rule.InternalResolutionMinutes / 60.0),
            ExternalHours = (int)Math.Ceiling(rule.ExternalResolutionMinutes / 60.0),
            FirstResponseMinutes = rule.FirstResponseMinutes
        };
    }

    public async Task<PrioritySlaRule> GetActivePrioritySlaRuleAsync(string priority, CancellationToken ct = default)
    {
        var lowered = priority.Trim().ToLower();
        var rule = await _context.PrioritySlaRules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Priority.ToLower() == lowered, ct);

        // No built-in numbers to fall back on: a missing rule is a configuration error and must be loud.
        return rule ?? throw new InvalidOperationException(
            $"No SLA rule is configured for priority '{priority}'. Configure it under SLA Maintenance.");
    }

    public async Task<CaseEscalationStatusDto?> GetCaseEscalationStatusAsync(Guid caseId, CancellationToken ct = default)
    {
        var c = await _context.Cases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == caseId, ct);
        if (c == null) return null;

        var policy = await _escalation.GetPolicyAsync(ct);
        var effective = policy.EffectiveLevel(c);
        var nextConfig = policy.Above(effective);
        bool isMax = nextConfig == null;
        var currentName = effective > 0 ? policy.NameOf(effective) : "Not escalated";

        return new CaseEscalationStatusDto
        {
            CaseId = c.Id,
            CaseNumber = c.CaseNumber,
            CurrentLevel = effective,
            CurrentLevelName = currentName,
            NextLevel = nextConfig?.LevelNumber,
            NextLevelName = nextConfig?.Name,
            NextTargetRole = nextConfig == null ? null : string.Join(", ", EscalationLevelRules.RoleNames(nextConfig)),
            IsMaxLevel = isMax,
            MaxLevelNotice = isMax ? (policy.ActiveLevels.Count == 0
                ? "No complete escalation level is configured. An administrator sets them up under Escalation Maintenance."
                : $"This case has reached the highest configured escalation level ({currentName}). Further escalation is not allowed.") : null,
            TriggerDescription = nextConfig != null ? EscalationTriggers.Describe(nextConfig.TriggerType, nextConfig.TriggerValue) : string.Empty,
            ActionDescription = nextConfig?.ActionDescription ?? string.Empty
        };
    }
}
