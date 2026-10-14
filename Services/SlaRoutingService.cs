namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
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

    public SlaRoutingService(AppDbContext context, ILogger<SlaRoutingService> logger, IEscalationService escalation)
    {
        _context = context;
        _logger = logger;
        _escalation = escalation;
    }

    // ===================================================================================== escalation validation

    /// <summary>
    /// Checks a level's structured trigger and assignment before it is stored. A level is never saved in a state the
    /// engine cannot execute, and its description is always derived from the structure so text and behaviour cannot drift.
    /// </summary>
    private async Task ValidateLevelAsync(string name, string triggerType, decimal? triggerValue, string assignmentType, string? targetRole, Guid? targetUserId, CancellationToken ct)
    {
        var triggerError = EscalationTriggers.Validate(triggerType, triggerValue);
        if (triggerError != null) throw new InvalidOperationException($"{name}: {triggerError}");

        if (!EscalationService.AssignmentTypes.Contains(assignmentType, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{name}: '{assignmentType}' is not an assignment type. Choose one of: {string.Join(", ", EscalationService.AssignmentTypes)}.");

        if (string.Equals(assignmentType, EscalationService.AssignmentRole, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(targetRole))
            throw new InvalidOperationException($"{name}: choose the role that receives the escalation.");

        if (string.Equals(assignmentType, EscalationService.AssignmentUser, StringComparison.OrdinalIgnoreCase))
        {
            if (!targetUserId.HasValue) throw new InvalidOperationException($"{name}: choose the person who receives the escalation.");
            if (!await _context.Users.AnyAsync(u => u.Id == targetUserId.Value && u.IsActive, ct))
                throw new InvalidOperationException($"{name}: the chosen person is not an active user.");
        }
    }

    private static string CanonicalAssignment(string value) =>
        EscalationService.AssignmentTypes.First(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Keeps every case on a level that exists after the matrix changed.</summary>
    private async Task ClampCaseLevelsAsync(CancellationToken ct)
    {
        var max = await _context.EscalationLevelConfigs.Select(l => (int?)l.LevelNumber).MaxAsync(ct) ?? 1;
        await _context.Cases.Where(c => c.EscalationLevel > max).ExecuteUpdateAsync(u => u.SetProperty(c => c.EscalationLevel, max), ct);
    }

    public async Task<SlaRoutingConfigResponseDto> GetFullConfigurationAsync(CancellationToken ct = default)
    {
        // 1. Priority SLA Rules & Mappings (ordered by the administrator-defined display order)
        var rules = await _context.PrioritySlaRules
            .Include(r => r.CategoryMappings)
            .AsNoTracking()
            .OrderBy(r => r.DisplayOrder).ThenBy(r => r.Priority)
            .ToListAsync(ct);

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
            AppliedSubCategoryIds = r.CategoryMappings.Select(m => m.DepartmentSubCategoryId).ToList()
        }).ToList();

        // 2. Sub-categories a priority can be applied to: every active one, plus any that is already
        //    mapped (so a mapping never silently disappears from the screen).
        var mappedIds = rules.SelectMany(r => r.CategoryMappings).Select(m => m.DepartmentSubCategoryId).ToHashSet();
        var categories = await _context.DepartmentSubCategories
            .AsNoTracking()
            .Where(s => s.IsActive || mappedIds.Contains(s.Id))
            .OrderBy(s => s.Department.Name)
            .ThenBy(s => s.Name)
            .Select(s => new CategoryOptionDto
            {
                Id = s.Id,
                Name = s.Name,
                DepartmentId = s.DepartmentId,
                DepartmentName = s.Department.Name,
                IsActive = s.IsActive
            })
            .ToListAsync(ct);

        // 3. Business Hours (7 Days)
        var businessHoursRaw = await _context.BusinessHours
            .AsNoTracking()
            .ToListAsync(ct);

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

        // 4. Public Holidays
        var holidays = await _context.PublicHolidays
            .AsNoTracking()
            .OrderBy(h => h.HolidayDate)
            .Select(h => new PublicHolidayDto
            {
                Id = h.Id,
                HolidayDate = h.HolidayDate,
                Name = h.Name,
                IsActive = h.IsActive
            })
            .ToListAsync(ct);

        // 5. Escalation Levels
        var levels = await _context.EscalationLevelConfigs
            .Include(l => l.TargetUser)
            .AsNoTracking()
            .OrderBy(l => l.LevelNumber)
            .Select(l => new EscalationLevelConfigDto
            {
                Id = l.Id,
                LevelNumber = l.LevelNumber,
                Name = l.Name,
                AssignmentType = l.AssignmentType,
                TargetRole = l.TargetRole,
                TargetUserId = l.TargetUserId,
                TargetUserName = l.TargetUser != null ? l.TargetUser.Name : null,
                TriggerType = l.TriggerType,
                TriggerValue = l.TriggerValue,
                TriggerDescription = l.TriggerDescription,
                ActionDescription = l.ActionDescription,
                ReassignOwner = l.ReassignOwner,
                IsActive = l.IsActive
            })
            .ToListAsync(ct);
        foreach (var l in levels) l.TriggerDescription = EscalationTriggers.Describe(l.TriggerType, l.TriggerValue);

        // 6. Available Users and Roles for UI assignment selector
        var users = await _context.Users
            .AsNoTracking()
            .OrderBy(u => u.Name)
            .Select(u => new UserOptionDto
            {
                Id = u.Id,
                Name = u.Name,
                Role = u.Role,
                Team = string.Join(", ", _context.TeamMembers.Where(m => m.UserId == u.Id && m.IsActive).OrderBy(m => m.Department.Name).Select(m => m.Department.Name))
            })
            .ToListAsync(ct);

        // Roles come from the Host's users (plus any a level already points at, so the editor can still show it).
        var roles = users
            .Select(u => u.Role)
            .Concat(levels.Where(l => string.Equals(l.AssignmentType, EscalationService.AssignmentRole, StringComparison.OrdinalIgnoreCase)).Select(l => l.TargetRole))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r)
            .ToList();

        var timeZoneId = await _context.BusinessCalendarSettings.AsNoTracking().Select(t => t.TimeZoneId).FirstOrDefaultAsync(ct) ?? string.Empty;

        return new SlaRoutingConfigResponseDto
        {
            PriorityRules = ruleDtos,
            AvailableCategories = categories,
            BusinessHours = businessHours,
            PublicHolidays = holidays,
            EscalationLevels = levels,
            AvailableRoles = roles,
            AvailableUsers = users,
            TimeZoneId = timeZoneId,
            AvailableTimeZones = TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Where(id => id.Contains('/')).OrderBy(id => id).ToList(),
            TriggerTypes = EscalationTriggers.All.Select(t => new EscalationTriggerOptionDto
            {
                Type = t.Type, Label = t.Label, NeedsValue = t.NeedsValue, Unit = t.Unit, Min = t.Min, Max = t.Max,
            }).ToList(),
            AssignmentTypes = EscalationService.AssignmentTypes.ToList(),
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
                }

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
                        .Select(s => s.Department.Name + " / " + s.Name)
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

                // 1b. Time zone of the working calendar
                if (!string.IsNullOrWhiteSpace(request.TimeZoneId))
                {
                    BusinessTimeService.FindTimeZone(request.TimeZoneId);   // throws a clear error for an unknown zone
                    var setting = await _context.BusinessCalendarSettings.FirstOrDefaultAsync(ct);
                    if (setting == null)
                        _context.BusinessCalendarSettings.Add(new BusinessCalendarSetting { Id = Guid.NewGuid(), TimeZoneId = request.TimeZoneId.Trim(), CreatedAt = now });
                    else if (setting.TimeZoneId != request.TimeZoneId.Trim())
                    {
                        setting.TimeZoneId = request.TimeZoneId.Trim();
                        setting.UpdatedAt = now;
                    }
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

                // 3. Update Escalation Level Configs (validated; descriptions derived from the structured trigger)
                var inputLevels = request.EscalationLevels.OrderBy(l => l.LevelNumber).ToList();
                if (inputLevels.GroupBy(l => l.LevelNumber).Any(g => g.Count() > 1))
                    throw new InvalidOperationException("Escalation level numbers must be unique.");
                if (inputLevels.Any(l => l.LevelNumber < 1))
                    throw new InvalidOperationException("Escalation level numbers start at 1.");
                foreach (var l in inputLevels)
                {
                    var label = string.IsNullOrWhiteSpace(l.Name) ? $"Level {l.LevelNumber}" : l.Name;
                    await ValidateLevelAsync(label, l.TriggerType, l.TriggerValue, l.AssignmentType ?? EscalationService.AssignmentRole, l.TargetRole, l.TargetUserId, ct);
                }

                var existingLevels = await _context.EscalationLevelConfigs.ToListAsync(ct);
                _context.EscalationLevelConfigs.RemoveRange(existingLevels);
                await _context.SaveChangesAsync(ct);

                foreach (var inputLvl in inputLevels)
                {
                    var triggerType = EscalationTriggers.Canonical(inputLvl.TriggerType)!;
                    var assignment = CanonicalAssignment(inputLvl.AssignmentType ?? EscalationService.AssignmentRole);
                    var needsValue = EscalationTriggers.Find(triggerType)!.NeedsValue;
                    _context.EscalationLevelConfigs.Add(new EscalationLevelConfig
                    {
                        Id = Guid.NewGuid(),
                        LevelNumber = inputLvl.LevelNumber,
                        Name = string.IsNullOrWhiteSpace(inputLvl.Name) ? $"Level {inputLvl.LevelNumber}" : inputLvl.Name.Trim(),
                        AssignmentType = assignment,
                        TargetRole = inputLvl.TargetRole?.Trim() ?? string.Empty,
                        TargetUserId = assignment == EscalationService.AssignmentUser ? inputLvl.TargetUserId : null,
                        TriggerType = triggerType,
                        TriggerValue = needsValue ? inputLvl.TriggerValue : null,
                        TriggerDescription = EscalationTriggers.Describe(triggerType, needsValue ? inputLvl.TriggerValue : null),
                        ActionDescription = inputLvl.ActionDescription ?? string.Empty,
                        ReassignOwner = inputLvl.ReassignOwner,
                        DisplayOrder = inputLvl.LevelNumber,
                        IsActive = inputLvl.IsActive,
                        CreatedAt = now
                    });
                }
                await _context.SaveChangesAsync(ct);
                await ClampCaseLevelsAsync(ct);

                // 4. Record Audit Log in CaseEvents
                _context.CaseEvents.Add(new CaseEvent
                {
                    Id = Guid.NewGuid(),
                    CaseId = null,
                    EventType = EventType.Other,
                    Module = "SlaEscalation",
                    EntityName = "Cases SLA & Routing Configuration",
                    ActionType = "UPDATE_CONFIG",
                    Message = "Administrator saved updated Cases SLA Matrix, Business Hours, and Escalation Matrix.",
                    UserId = actingUserId,
                    CreatedAt = now,
                    IsInternal = true
                });

                await _context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(ct);
                _logger.LogError(ex, "Failed to save Cases SLA & Routing configuration atomically.");
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

    private static EscalationLevelConfigDto ToDto(EscalationLevelConfig l, string? targetUserName = null) => new()
    {
        Id = l.Id,
        LevelNumber = l.LevelNumber,
        Name = l.Name,
        AssignmentType = l.AssignmentType,
        TargetRole = l.TargetRole,
        TargetUserId = l.TargetUserId,
        TargetUserName = targetUserName,
        TriggerType = l.TriggerType,
        TriggerValue = l.TriggerValue,
        TriggerDescription = EscalationTriggers.Describe(l.TriggerType, l.TriggerValue),
        ActionDescription = l.ActionDescription,
        ReassignOwner = l.ReassignOwner,
        IsActive = l.IsActive
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
        var existing = await _context.EscalationLevelConfigs.OrderBy(l => l.LevelNumber).ToListAsync(ct);
        int levelNumber = dto.LevelNumber.HasValue && dto.LevelNumber.Value > 0
            ? dto.LevelNumber.Value
            : (existing.Count > 0 ? existing.Max(l => l.LevelNumber) + 1 : 1);
        if (existing.Any(l => l.LevelNumber == levelNumber))
            throw new InvalidOperationException($"Level {levelNumber} already exists.");

        string name = string.IsNullOrWhiteSpace(dto.Name) ? $"Level {levelNumber}" : dto.Name.Trim();
        var assignmentType = string.IsNullOrWhiteSpace(dto.AssignmentType) ? EscalationService.AssignmentRole : dto.AssignmentType;
        await ValidateLevelAsync(name, dto.TriggerType, dto.TriggerValue, assignmentType, dto.TargetRole, dto.TargetUserId, ct);

        var triggerType = EscalationTriggers.Canonical(dto.TriggerType)!;
        var needsValue = EscalationTriggers.Find(triggerType)!.NeedsValue;
        var level = new EscalationLevelConfig
        {
            Id = Guid.NewGuid(),
            LevelNumber = levelNumber,
            Name = name,
            AssignmentType = CanonicalAssignment(assignmentType),
            TargetRole = dto.TargetRole?.Trim() ?? string.Empty,
            TargetUserId = string.Equals(assignmentType, EscalationService.AssignmentUser, StringComparison.OrdinalIgnoreCase) ? dto.TargetUserId : null,
            TriggerType = triggerType,
            TriggerValue = needsValue ? dto.TriggerValue : null,
            ActionDescription = string.IsNullOrWhiteSpace(dto.ActionDescription) ? $"Escalate to {dto.TargetRole}" : dto.ActionDescription.Trim(),
            ReassignOwner = dto.ReassignOwner,
            DisplayOrder = levelNumber,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        level.TriggerDescription = EscalationTriggers.Describe(level.TriggerType, level.TriggerValue);

        _context.EscalationLevelConfigs.Add(level);
        AuditLevel("CREATE", $"Created escalation level '{level.Name}' (target: {level.AssignmentType} {level.TargetRole}, trigger: {level.TriggerDescription}).", actingUserId);
        await _context.SaveChangesAsync(ct);

        return ToDto(level);
    }

    public async Task<EscalationLevelConfigDto?> UpdateEscalationLevelAsync(Guid id, UpdateEscalationLevelDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        var level = await _context.EscalationLevelConfigs.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (level == null) return null;

        var name = string.IsNullOrWhiteSpace(dto.Name) ? level.Name : dto.Name.Trim();
        var triggerType = dto.TriggerType ?? level.TriggerType;
        var triggerValue = dto.TriggerType != null || dto.TriggerValue.HasValue ? dto.TriggerValue : level.TriggerValue;
        var assignment = dto.AssignmentType ?? level.AssignmentType;
        var targetRole = dto.TargetRole ?? level.TargetRole;
        var targetUserId = dto.TargetUserId ?? level.TargetUserId;

        await ValidateLevelAsync(name, triggerType, triggerValue, assignment, targetRole, targetUserId, ct);

        var canonical = EscalationTriggers.Canonical(triggerType)!;
        var needsValue = EscalationTriggers.Find(canonical)!.NeedsValue;
        level.Name = name;
        level.TriggerType = canonical;
        level.TriggerValue = needsValue ? triggerValue : null;
        level.AssignmentType = CanonicalAssignment(assignment);
        level.TargetRole = targetRole.Trim();
        level.TargetUserId = level.AssignmentType == EscalationService.AssignmentUser ? targetUserId : null;
        if (dto.ActionDescription != null) level.ActionDescription = dto.ActionDescription.Trim();
        if (dto.ReassignOwner.HasValue) level.ReassignOwner = dto.ReassignOwner.Value;
        if (dto.IsActive.HasValue) level.IsActive = dto.IsActive.Value;
        level.TriggerDescription = EscalationTriggers.Describe(level.TriggerType, level.TriggerValue);
        level.UpdatedAt = DateTime.UtcNow;

        AuditLevel("UPDATE", $"Updated escalation level '{level.Name}' (target: {level.AssignmentType} {level.TargetRole}, trigger: {level.TriggerDescription}, {(level.IsActive ? "active" : "inactive")}).", actingUserId);
        await _context.SaveChangesAsync(ct);

        return ToDto(level);
    }

    public async Task<bool> DeleteEscalationLevelAsync(Guid id, Guid actingUserId, CancellationToken ct = default)
    {
        var level = await _context.EscalationLevelConfigs.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (level == null) return false;

        var removedNumber = level.LevelNumber;
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

        // Cases keep pointing at the level they were on: those past the deleted one move down with the renumbering,
        // and one sitting on the deleted level falls back to the level before it.
        // (Order matters: settle the cases ON the deleted level first, or the renumbering would sweep them up too.)
        await _context.Cases.Where(c => c.EscalationLevel == removedNumber && removedNumber > 1)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.EscalationLevel, removedNumber - 1), ct);
        await _context.Cases.Where(c => c.EscalationLevel > removedNumber)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.EscalationLevel, c => c.EscalationLevel - 1), ct);
        await ClampCaseLevelsAsync(ct);

        return true;
    }

    /// <summary>
    /// Finds the active sub-category (and the priority configured for it) a new case would belong to.
    /// Both the match and the mapping are by ID — names only locate the sub-category within its department.
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
            $"No SLA rule is configured for priority '{priority}'. Configure it under Cases SLA & Routing.");
    }

    public async Task<CaseEscalationStatusDto?> GetCaseEscalationStatusAsync(Guid caseId, CancellationToken ct = default)
    {
        var c = await _context.Cases
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == caseId, ct);
        if (c == null) return null;

        var policy = await _escalation.GetPolicyAsync(ct);
        int currentLevel = c.EscalationLevel > 0 ? c.EscalationLevel : 1;
        var currentConfig = policy.ActiveLevels.FirstOrDefault(l => l.LevelNumber == currentLevel);
        var nextConfig = _escalation.NextLevel(c, policy);
        bool isMax = nextConfig == null;
        var nextUser = nextConfig != null ? await _escalation.ResolveTargetAsync(c, nextConfig, ct) : null;

        return new CaseEscalationStatusDto
        {
            CaseId = c.Id,
            CaseNumber = c.CaseNumber,
            CurrentLevel = currentLevel,
            CurrentLevelName = currentConfig?.Name ?? $"Level {currentLevel}",
            NextLevel = nextConfig?.LevelNumber,
            NextLevelName = nextConfig?.Name,
            NextTargetRole = nextConfig?.TargetRole,
            NextTargetUserName = nextUser?.Name,
            NextTargetUserId = nextUser?.Id,
            IsMaxLevel = isMax,
            MaxLevelNotice = isMax ? $"This case has reached the maximum configured escalation level ({currentConfig?.Name ?? $"Level {currentLevel}"}). Further escalation is not allowed." : null,
            TriggerDescription = nextConfig != null ? EscalationTriggers.Describe(nextConfig.TriggerType, nextConfig.TriggerValue) : string.Empty,
            ActionDescription = nextConfig?.ActionDescription ?? string.Empty
        };
    }
}
