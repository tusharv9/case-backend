namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;

using CaseManagement.Api.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

public class CaseService : ICaseService
{
    private readonly ICaseRepository _caseRepository;
    private readonly INotificationService _notificationService;
    private readonly IConfigurableSettingsService _settingsService;
    private readonly AppDbContext _context;
    private readonly IPiiMaskingService? _piiMasking;
    private readonly IBusinessTimeService? _businessTimeService;
    private readonly ISlaRoutingService? _slaRoutingService;
    private readonly IRoutingEngineService? _routingEngine;
    private readonly IFieldValidationEngine? _fieldValidation;
    private readonly ISlaClockProvider? _slaClockProvider;
    private readonly IEscalationService? _escalation;
    private readonly IMentionService _mentions;
    private readonly ILogger<CaseService> _logger;
    private readonly IConfigCache? _configCache;
    private readonly IAssignmentEligibilityService _eligibility;

    public CaseService(
        ICaseRepository caseRepository,
        INotificationService notificationService,
        IConfigurableSettingsService settingsService,
        AppDbContext context,
        IWebHostEnvironment env,   // no longer used (attachments moved to AttachmentService); kept so existing callers compile
        IOptions<AttachmentOptions>? attachmentOptions = null,   // likewise
        IPiiMaskingService? piiMasking = null,
        IBusinessTimeService? businessTimeService = null,
        ISlaRoutingService? slaRoutingService = null,
        IRoutingEngineService? routingEngine = null,
        IFieldValidationEngine? fieldValidation = null,
        ISlaClockProvider? slaClockProvider = null,
        IEscalationService? escalation = null,
        IMentionService? mentions = null,
        ILogger<CaseService>? logger = null,
        IConfigCache? configCache = null,
        IAssignmentEligibilityService? eligibility = null)
    {
        _configCache = configCache;
        _eligibility = eligibility ?? new AssignmentEligibilityService(context, configCache);
        _caseRepository = caseRepository;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _context = context;
        _piiMasking = piiMasking;
        _businessTimeService = businessTimeService;
        _slaRoutingService = slaRoutingService;
        _routingEngine = routingEngine;
        _fieldValidation = fieldValidation;
        _slaClockProvider = slaClockProvider;
        _escalation = escalation;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CaseService>.Instance;
        _mentions = mentions ?? new MentionService(context, notificationService, Microsoft.Extensions.Logging.Abstractions.NullLogger<MentionService>.Instance);
    }

    /// <summary>The SLA clock bound to the configured calendar. (Without a provider — unit tests only — time is wall-clock.)</summary>
    private async Task<SlaClock> GetClockAsync(CancellationToken ct = default) =>
        _slaClockProvider != null
            ? await _slaClockProvider.GetAsync(ct)
            : new SlaClock(BusinessCalendar.RoundTheClock, null);

    /// <summary>
    /// Executes a unit of work inside a user-initiated transaction wrapped in the configured
    /// execution strategy (e.g. NpgsqlRetryingExecutionStrategy), as required by EF Core when
    /// retry-on-failure is enabled.
    /// </summary>
    private async Task ExecuteInTransactionAsync(Func<Task> action)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                await action();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

    private async Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var result = await action();
                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

    public async Task<Case> CreateCaseAsync(CreateCaseDto dto, Guid createdByUserId)
    {
        // Everything the form submitted is checked against the administrator's configuration first; what comes
        // back is canonical (configured spelling, resolved records), so nothing downstream guesses or defaults.
        var validated = await ValidateCreateCaseAsync(dto);

        // 1. Resolve the priority: the sub-category's configured priority is authoritative; otherwise the
        //    requested one, which must be a configured priority. Never silently defaulted.
        var slaRouting = _slaRoutingService ?? throw new InvalidOperationException("SLA & routing configuration is not available.");
        var resolution = await slaRouting.ResolveEffectivePriorityAsync(validated.Department.Id, validated.SubCategory.Name, dto.Severity);
        var effectivePriority = resolution.Priority;

        // The case type's configuration supplies the case-number prefix.
        string canonicalCaseType = validated.CaseType.Name;
        string prefix = validated.CaseType.Prefix;

        int seq = await _caseRepository.GetNextCaseSequenceAsync();
        string caseNumber = $"{prefix}{seq:D5}";

        // 2. SLA targets & version come from the priority's rule and are snapshotted onto the case, so later
        //    configuration changes never rewrite history. A missing rule is a configuration error, not a default.
        var slaRule = await slaRouting.GetActivePrioritySlaRuleAsync(effectivePriority);

        DateTime now = DateTime.UtcNow;

        // Configured values only: a field the administrator made optional and the user left blank stays blank.
        string sourceChannel = validated.Value("sourceChannel");
        string preferredChannel = validated.Value("preferredCommunicationChannel");

        var newCase = new Case
        {
            CaseNumber = caseNumber,
            CaseType = canonicalCaseType,
            Title = dto.Title.Trim(),
            Description = dto.Description?.Trim() ?? string.Empty,
            CustomerId = dto.CustomerId,
            DepartmentId = validated.Department.Id,
            OwnerId = createdByUserId,
            Severity = effectivePriority,
            Status = CaseStatus.Open,
            SourceChannel = sourceChannel,
            PreferredCommunicationChannel = preferredChannel,
            CommunicationChannel = sourceChannel,
            Subcategory = validated.SubCategory.Name,
        };

        // 3. Start the SLA clock: targets are snapshotted from the rule, due dates are computed in business time.
        (await GetClockAsync()).Start(newCase, slaRule, now);

        foreach (var (key, value) in validated.CustomAttributes)
        {
            newCase.CustomAttributes.Add(new CaseCustomAttribute
            {
                Id = Guid.NewGuid(),
                CaseId = newCase.Id,
                FieldKey = key,
                FieldValue = value,
                CreatedAt = DateTime.UtcNow
            });
        }

        // 4. Routing and agent assignment run INSIDE the same transaction that stores the case. The routing engine serialises
        //    assignments per team for the life of this transaction, so concurrent cases cannot be handed to the same
        //    "least loaded" agent, and the round-robin pointer commits (or rolls back) together with the case.
        RoutingDecisionResult? routingDecision = null;
        var customer = _routingEngine != null
            ? await _context.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == dto.CustomerId)
            : null;

        await ExecuteInTransactionAsync(async () =>
        {
            if (_routingEngine != null)
                routingDecision = await _routingEngine.RouteAndAssignCaseAsync(newCase, customer);

            await _caseRepository.AddAsync(newCase);
            await _caseRepository.AddEventAsync(new CaseEvent
            {
                CaseId = newCase.Id,
                EventType = EventType.Create,
                Message = $"Case opened ({canonicalCaseType}).",
                CreatedAt = DateTime.UtcNow,
                UserId = createdByUserId
            });
            if (routingDecision != null && !string.IsNullOrWhiteSpace(routingDecision.RoutingLogMessage))
            {
                await _caseRepository.AddEventAsync(new CaseEvent
                {
                    CaseId = newCase.Id,
                    EventType = EventType.Assign,
                    Message = routingDecision.RoutingLogMessage,
                    CreatedAt = DateTime.UtcNow,
                    UserId = createdByUserId
                });
            }
        });

        // No agent could take it: the team lead holds it, and says so — a case must never sit unnoticed.
        if (routingDecision?.HeldReason != null && routingDecision.HeldByUserId.HasValue)
        {
            try
            {
                await _notificationService.CreateNotificationAsync(
                    routingDecision.HeldByUserId.Value,
                    "CASE_UNASSIGNED",
                    "Case needs an agent",
                    $"Case {newCase.CaseNumber} ({newCase.Title}) could not be assigned automatically. {routingDecision.HeldReason}",
                    newCase.Id,
                    newCase.CaseNumber,
                    "High",
                    0,
                    default,
                    $"unassigned:{newCase.Id}");
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Notification Trigger Error"); }
        }

        try
        {
            if (routingDecision?.HeldReason != null) return newCase;   // the lead was told it needs an agent instead

            await _notificationService.CreateNotificationAsync(
                newCase.OwnerId,
                "CASE_ASSIGNED",
                "New Case Assigned",
                $"Case {newCase.CaseNumber} ({newCase.Title}) has been created and assigned to you.",
                newCase.Id,
                newCase.CaseNumber
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification Trigger Error");
        }

        return newCase;
    }

    public async Task<CaseDetailDto?> GetCaseDetailsAsync(Guid caseId, CancellationToken ct = default)
    {
        // Read-only: SLA monitoring runs in SlaEscalationBackgroundService, never on a read.
        var caseDetail = await _caseRepository.GetCaseDetailAsync(caseId, ct);
        // The case's own masked fields (custom attributes), the customer's name it repeats, the customer and its sub-cases.
        if (caseDetail != null && _piiMasking != null)
        {
            await _piiMasking.MaskCaseAsync(caseDetail, ct);
        }
        if (caseDetail != null)
        {
            (await GetClockAsync(ct)).Enrich(new CaseSummaryDto?[] { caseDetail }.Concat(caseDetail.Subcases ?? new List<CaseSummaryDto>()));
        }
        return caseDetail;
    }

    private async Task EnrichSlaAsync(IEnumerable<CaseSummaryDto>? cases, CancellationToken ct)
    {
        if (cases == null) return;
        (await GetClockAsync(ct)).Enrich(cases);
    }

    public async Task<IEnumerable<SearchCaseHitDto>> SearchCasesAsync(string query, int limit, CancellationToken ct = default)
    {
        return await _caseRepository.SearchCasesAsync(query, limit, ct);
    }

    public async Task<PagedResponseDto<CaseSummaryDto>> GetPaginatedBoardCasesAsync(
        string? status = null,
        int page = 1,
        int pageSize = 30,
        Guid? departmentId = null,
        string? caseType = null,
        string? search = null,
        string? priority = null,
        string? channel = null,
        CancellationToken ct = default)
    {
        var paged = await _caseRepository.GetPaginatedBoardCasesAsync(
            status,
            page,
            pageSize,
            departmentId,
            caseType,
            search,
            priority,
            channel,
            ct);

        await EnrichSlaAsync(paged.Items, ct);
        if (_piiMasking != null) await _piiMasking.MaskCasesAsync(paged.Items, ct);   // the list repeats the customer's name
        return paged;
    }

    public async Task UpdateCaseStatusAsync(Guid caseId, UpdateCaseStatusDto dto)
    {
        var existingCase = await _caseRepository.GetByIdAsync(caseId);
        if (existingCase == null) throw new KeyNotFoundException("Case not found");

        if (existingCase.Status == CaseStatus.Resolved)
            throw new InvalidOperationException("Cannot update status of a resolved case. Use Reopen instead.");

        string rawStatus = dto.Status?.Trim() ?? "";
        CaseStatus newStatus;
        if (string.Equals(rawStatus, "Waiting on Customer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawStatus, "WaitingOnCustomer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawStatus, "Waiting_On_Customer", StringComparison.OrdinalIgnoreCase))
        {
            newStatus = CaseStatus.WaitingOnCustomer;
        }
        else if (string.Equals(rawStatus, "In Progress", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(rawStatus, "InProgress", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(rawStatus, "In_Progress", StringComparison.OrdinalIgnoreCase))
        {
            newStatus = CaseStatus.InProgress;
        }
        else if (!Enum.TryParse<CaseStatus>(rawStatus.Replace(" ", "").Replace("_", ""), true, out newStatus) || !Enum.IsDefined(newStatus))
        {
            throw new InvalidOperationException($"'{rawStatus}' is not a valid case status.");
        }
        
        if (existingCase.Status == newStatus)
            throw new InvalidOperationException($"Case is already in '{newStatus}' status.");

        // --- Status Transition Validation ---
        // Only approved transitions are allowed. Resolved must go through ResolveCaseAsync,
        // Escalated must go through EscalateCaseAsync.
        if (newStatus == CaseStatus.Resolved)
            throw new InvalidOperationException("Use the Resolve workflow to resolve a case.");

        if (newStatus == CaseStatus.Escalated)
            throw new InvalidOperationException("Use the Escalate workflow to escalate a case.");

        if (!IsValidTransition(existingCase.Status, newStatus))
            throw new InvalidOperationException(
                $"Cannot transition from '{existingCase.Status}' to '{newStatus}'. " +
                $"Allowed transitions from '{existingCase.Status}': {string.Join(", ", GetAllowedTransitions(existingCase.Status))}.");


        var oldStatus = existingCase.Status;

        // SLA clock: Waiting on Customer pauses it, leaving that status resumes it, resolving stops it, and the first move
        // to In Progress counts as the first response. The clock owns what each of those means for the due dates.
        var clock = await GetClockAsync();
        var transitionAt = DateTime.UtcNow;

        if (newStatus == CaseStatus.WaitingOnCustomer)
            clock.Pause(existingCase, transitionAt);
        else if (oldStatus == CaseStatus.WaitingOnCustomer)
            clock.Resume(existingCase, transitionAt);

        existingCase.Status = newStatus;

        if (newStatus == CaseStatus.InProgress)
            clock.RecordFirstResponse(existingCase, transitionAt);

        if (newStatus == CaseStatus.Escalated && existingCase.EscalationLevel < 2)
        {
            existingCase.EscalationLevel = 2;
        }

        if (newStatus == CaseStatus.Resolved)
        {
            existingCase.ResolvedAt = transitionAt;
            clock.Stop(existingCase, transitionAt);
        }

        var eventType = newStatus switch
        {
            CaseStatus.Resolved => EventType.Resolve,
            CaseStatus.Escalated => EventType.Escalate,
            _ => EventType.Note
        };

        var statusLabel = newStatus == CaseStatus.WaitingOnCustomer ? "Waiting on Customer" : (newStatus == CaseStatus.InProgress ? "In Progress" : newStatus.ToString());
        var caseEvent = new CaseEvent
        {
            CaseId = caseId,
            EventType = eventType,
            Message = dto.Note ?? $"Status changed to {statusLabel}",
            CreatedAt = DateTime.UtcNow,
            UserId = dto.UserId
        };

        await ExecuteInTransactionAsync(async () =>
        {
            await _caseRepository.UpdateAsync(existingCase);
            await _caseRepository.AddEventAsync(caseEvent);
        });

        try
        {
            await _notificationService.CreateNotificationAsync(
                existingCase.OwnerId,
                newStatus == CaseStatus.Resolved ? "CASE_RESOLVED" : "CASE_STATUS_CHANGED",
                newStatus == CaseStatus.Resolved ? "Case Resolved" : "Case Status Updated",
                $"Case {existingCase.CaseNumber} status changed to {newStatus}. {(dto.Note != null ? dto.Note : "")}".Trim(),
                existingCase.Id,
                existingCase.CaseNumber
            );
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification Trigger Error"); }
    }

    public async Task AssignCaseAsync(Guid caseId, AssignCaseDto dto, bool callerMayAssignAnyCase = false)
    {
        var existingCase = await _caseRepository.GetByIdAsync(caseId);
        if (existingCase == null) throw new KeyNotFoundException("Case not found");

        if (existingCase.Status is CaseStatus.Resolved or CaseStatus.Closed or CaseStatus.Cancelled)
            throw new InvalidOperationException($"Cannot assign a case in '{existingCase.Status}' status.");

        // Who may change the holder is checked before anything about the target is revealed.
        await _eligibility.EnsureMayReassignAsync(existingCase, dto.UserId, callerMayAssignAnyCase);

        if (existingCase.OwnerId == dto.OwnerId)
            throw new InvalidOperationException($"{existingCase.Owner?.Name ?? "The current owner"} is already handling this case.");

        var newOwner = await _context.Users.FindAsync(dto.OwnerId);
        if (newOwner == null)
            throw new KeyNotFoundException("Assigned user not found.");

        var reason = await CaseReasons.ResolveAsync(_context, CaseReasons.Assignment, dto.Reason, dto.ReasonDetail);

        var caseEvent = new CaseEvent
        {
            CaseId = caseId,
            EventType = EventType.Assign,
            Message = $"Reassigned to {newOwner.Name}. Reason: {reason.Text}.",
            CreatedAt = DateTime.UtcNow,
            UserId = dto.UserId
        };

        await ExecuteInTransactionAsync(async () =>
        {
            // Same lock as automatic routing, so the capacity verdict below cannot be raced by a simultaneous assignment.
            await TeamAssignmentLock.AcquireAsync(_context, existingCase.DepartmentId);
            var policy = await _eligibility.GetManualPolicyAsync();
            var (team, verdict) = await _eligibility.CheckAsync(existingCase.DepartmentId, dto.OwnerId, policy);
            if (!verdict.IsEligible)
                throw new AssignmentNotAllowedException(verdict.Reason.ToString(), verdict.Explain(team.Team.Name));
            // Cross-team assignment is allowed by the settings AND needs the caller's permission.
            if (verdict.IsCrossTeam && !callerMayAssignAnyCase)
                throw new AssignmentForbiddenException((verdict with { Reason = IneligibilityReason.NeedsAssignPermission }).Explain(team.Team.Name));

            // Reassigning does not touch the SLA clock: the customer's wait started when the case was opened, whoever holds it.
            existingCase.OwnerId = dto.OwnerId;
            await _caseRepository.UpdateAsync(existingCase);
            await _caseRepository.AddEventAsync(caseEvent);
        });

        try
        {
            await _notificationService.CreateNotificationAsync(
                dto.OwnerId,
                "CASE_REASSIGNED",
                "Case Reassigned",
                $"Case {existingCase.CaseNumber} ({existingCase.Title}) has been assigned to you.",
                existingCase.Id,
                existingCase.CaseNumber
            );
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification Trigger Error"); }
    }

    public async Task<EligibleAssigneesDto> GetEligibleAssigneesAsync(Guid caseId, bool callerMayAssignAnyCase = false, CancellationToken ct = default)
    {
        var c = await _context.Cases.AsNoTracking()
            .Where(x => x.Id == caseId)
            .Select(x => new { x.Id, x.DepartmentId, x.OwnerId })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Case not found");

        var policy = await _eligibility.GetManualPolicyAsync(ct);
        var team = await _eligibility.EvaluateForCaseAsync(c.DepartmentId, policy, ct);
        return new EligibleAssigneesDto
        {
            CaseId = c.Id,
            DepartmentId = team.Team.Id,
            DepartmentName = team.Team.Name,
            DepartmentIsActive = team.Team.IsActive,
            CurrentOwnerId = c.OwnerId,
            Capacity = team.Capacity,
            CrossTeamMode = policy.CrossTeamMode,
            Candidates = team.Candidates
                .Select(v =>
                {
                    var isOwner = v.User.Id == c.OwnerId;
                    // Offered by the settings, but this caller may not reach outside the case's team.
                    if (v.IsEligible && v.IsCrossTeam && !callerMayAssignAnyCase) v = v with { Reason = IneligibilityReason.NeedsAssignPermission };
                    return new EligibleAssigneeDto
                    {
                        UserId = v.User.Id,
                        Name = v.User.Name,
                        Email = v.User.Email,
                        TeamRole = v.MemberRole,
                        IsTeamLead = v.IsTeamLead,
                        IsTeamMember = v.IsMember,
                        Status = v.User.Status.ToString(),
                        HomeTeamName = v.HomeTeamName,
                        IsCrossTeam = v.IsCrossTeam,
                        OpenCases = v.OpenCases,
                        IsCurrentOwner = isOwner,
                        // The current holder cannot be "assigned" again; everyone else carries the policy's verdict.
                        IsEligible = v.IsEligible && !isOwner,
                        IneligibleReason = isOwner ? "CurrentOwner" : v.IsEligible ? null : v.Reason.ToString(),
                        IneligibleMessage = isOwner ? $"{v.User.Name} already holds this case." : v.IsEligible ? null : v.Explain(team.Team.Name),
                    };
                })
                .OrderBy(x => x.IsCrossTeam)
                .ThenByDescending(x => x.IsEligible)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    public async Task AddNoteAsync(Guid caseId, AddNoteDto dto)
    {
        var existingCase = await _caseRepository.GetByIdAsync(caseId);
        if (existingCase == null) throw new KeyNotFoundException("Case not found");

        var caseEvent = new CaseEvent
        {
            CaseId = caseId,
            EventType = EventType.Note,
            Message = dto.Message,
            CreatedAt = DateTime.UtcNow,
            UserId = dto.UserId
        };
        await _caseRepository.AddEventAsync(caseEvent);

        try
        {
            await _notificationService.CreateNotificationAsync(
                existingCase.OwnerId,
                "CASE_NOTE_ADDED",
                "Note Added to Case",
                $"Note added to Case {existingCase.CaseNumber}: {dto.Message}",
                existingCase.Id,
                existingCase.CaseNumber
            );
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification Trigger Error"); }
    }

    public async Task<CaseStatsDto> GetCaseStatsAsync(Guid? departmentId, CancellationToken ct = default)
    {
        // "Open" = not resolved. "Breached" = open cases the SLA clock reads as breached — the same verdict every other
        // screen, the worker and the notifications use.
        var cases = _context.Cases.AsNoTracking().Where(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled);
        if (departmentId.HasValue) cases = cases.Where(c => c.DepartmentId == departmentId.Value);

        var health = await EvaluateHealthAsync(cases, ct);
        return new CaseStatsDto
        {
            OpenCount = health.Count,
            BreachedCount = health.Count(h => h == SlaHealth.Breached),
        };
    }

    /// <summary>The SLA health of every case in the query, from the one SLA clock.</summary>
    private async Task<List<SlaHealth>> EvaluateHealthAsync(IQueryable<Case> cases, CancellationToken ct)
    {
        var rows = await cases.Select(c => new SlaInputs
        {
            Status = c.Status,
            StartUtc = c.SlaStartTime,
            ResolvedAt = c.ResolvedAt,
            PausedAt = c.SlaPausedAt,
            PausedMinutes = c.SlaTotalPausedMinutes,
            FirstResponseTargetMinutes = c.FirstResponseTargetMinutes,
            InternalTargetMinutes = c.InternalResolutionTargetMinutes,
            ExternalTargetMinutes = c.ExternalResolutionTargetMinutes,
            FirstResponseActualAt = c.FirstResponseActualAt,
            FirstResponseStatus = c.FirstResponseStatus,
        }).ToListAsync(ct);

        var clock = await GetClockAsync(ct);
        var now = DateTime.UtcNow;
        return rows.Select(r => clock.Evaluate(r, now).Health).ToList();
    }

    public async Task TransferDepartmentAsync(Guid caseId, TransferDepartmentDto dto, Guid transferredByUserId, bool callerMayAssignAnyCase = false)
    {
        var existingCase = await _caseRepository.GetByIdAsync(caseId);
        if (existingCase == null) throw new KeyNotFoundException("Case not found");
        
        if (existingCase.Status is CaseStatus.Resolved or CaseStatus.Closed or CaseStatus.Cancelled)
            throw new InvalidOperationException($"Cannot transfer a case in '{existingCase.Status}' status.");

        await _eligibility.EnsureMayReassignAsync(existingCase, transferredByUserId, callerMayAssignAnyCase);

        // A transfer moves the case to another team; a team IS the queue, so there is nothing else to transfer to.
        if (dto.DepartmentId == Guid.Empty)
            throw new FieldValidationException(new[] { new FieldError("departmentId", "Choose the team to transfer the case to.") });
        if (dto.DepartmentId == existingCase.DepartmentId)
            throw new FieldValidationException(new[] { new FieldError("departmentId", "The case already belongs to this team. Choose a different team, or reassign it within the team instead.") });

        User? newOwner = null;
        var targetDept = await _context.Departments.FindAsync(dto.DepartmentId)
                         ?? throw new KeyNotFoundException("Target department not found.");
        if (!targetDept.IsActive)
            throw new AssignmentNotAllowedException(nameof(IneligibilityReason.TeamInactive), $"'{targetDept.Name}' is inactive and cannot receive cases.");
        const bool changesTeam = true;

        var reason = await CaseReasons.ResolveAsync(_context, CaseReasons.Transfer, dto.Reason, dto.ReasonDetail);

        // SLA TARGETS DO NOT RESET: Transfer keeps SLA clock running continuously without interruption
        var noteDesc = !string.IsNullOrWhiteSpace(dto.HandoverNote) ? $" Note: {dto.HandoverNote.Trim()}" : "";
        string BuildTransferMessage() => $"Transferred to {targetDept.Name}{(newOwner != null ? $" (Owner: {newOwner.Name})" : "")}. Reason: {reason.Text}.{noteDesc}";

        var caseEvent = new CaseEvent
        {
            CaseId = caseId,
            EventType = EventType.Transfer,
            Message = string.Empty,
            CreatedAt = DateTime.UtcNow,
            UserId = transferredByUserId
        };

        await ExecuteInTransactionAsync(async () =>
        {
            if (changesTeam)
            {
                existingCase.DepartmentId = targetDept.Id;

                // The receiving team's lead takes the case (as before). Without an active lead the team's own algorithm picks an
                // eligible agent — the same rules as routing at creation. Nobody at all: the transfer is refused rather than
                // leaving the case with someone outside its new team.
                var lead = targetDept.OwnerId.HasValue
                    ? await _context.Users.FirstOrDefaultAsync(u => u.Id == targetDept.OwnerId.Value && u.IsActive)
                    : null;
                if (lead != null)
                {
                    newOwner = lead;
                }
                else if (_routingEngine != null)
                {
                    var decision = await _routingEngine.AssignWithinTeamAsync(existingCase, existingCase.Customer, targetDept.Id, "a transfer");
                    newOwner = decision.AssignedUserId.HasValue ? await _context.Users.FindAsync(decision.AssignedUserId.Value) : null;
                }

                if (newOwner == null)
                    throw new AssignmentNotAllowedException("NoEligibleAssignee",
                        $"'{targetDept.Name}' has no active team lead and no available agent below capacity, so nobody could take this case.");
                existingCase.OwnerId = newOwner.Id;
            }

            caseEvent.Message = BuildTransferMessage();
            await _caseRepository.UpdateAsync(existingCase);
            await _caseRepository.AddEventAsync(caseEvent);
        });

        // Notify new owner/lead if assigned
        if (newOwner != null && newOwner.Id != transferredByUserId)
        {
            try
            {
                var requestingUser = await _context.Users.FindAsync(transferredByUserId);
                await _notificationService.CreateNotificationAsync(
                    newOwner.Id,
                    "CASE_TRANSFERRED",
                    "Case Transferred to Your Team",
                    $"Case {existingCase.CaseNumber} ({existingCase.Title}) was transferred to your team by {requestingUser?.Name ?? "Agent"} and is now assigned to you.",
                    caseId
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send transfer notification for case {CaseId}", caseId);
            }
        }
    }

    public async Task<LinkCaseResultDto> LinkCaseAsync(Guid caseId, LinkCaseDto dto)
    {
        var existingCase = await _caseRepository.GetByIdAsync(caseId);
        if (existingCase == null) throw new KeyNotFoundException("Case not found");
        
        var targetCase = await _caseRepository.GetByCaseNumberAsync(dto.TargetCaseNumber);
        if (targetCase == null) throw new KeyNotFoundException("Target case not found");

        if (existingCase.CaseNumber.Equals(dto.TargetCaseNumber, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot link a case to itself.");

        if (existingCase.LinkedCases.Any(lc => lc.TargetCaseId == targetCase.Id) ||
            existingCase.ChildRelations.Any(cr => cr.LinkedCaseId == targetCase.Id || (cr.ChildId != null && cr.ChildId.ToLower() == targetCase.CaseNumber.ToLower())))
        {
            throw new InvalidOperationException($"Case is already linked to {targetCase.CaseNumber}.");
        }

        string childId = string.Empty;
        Case newLinkSubcase = null!;

        await ExecuteInTransactionAsync(async () =>
        {
            // 1. Generate Sub-Case ID (e.g. C-00045-L01)
            int seq = await _caseRepository.GetNextChildSequenceAsync(caseId, ChildRelationType.Link);
            childId = $"{existingCase.CaseNumber}-L{seq:D2}";

            // 2. Create and Save NEW Child Subcase Record in Cases Table
            newLinkSubcase = new Case
            {
                Id = Guid.NewGuid(),
                CaseNumber = childId,
                CaseType = existingCase.CaseType,
                Title = $"[Linked Subcase] {existingCase.Title}",
                Description = $"Linked Subcase under Parent Case {existingCase.CaseNumber} referencing Case {targetCase.CaseNumber}. Reason: {dto.RelationshipType}",
                Status = CaseStatus.Open,
                Severity = existingCase.Severity,
                DepartmentId = existingCase.DepartmentId,
                CustomerId = existingCase.CustomerId,
                OwnerId = existingCase.OwnerId,
                ParentCaseId = existingCase.Id,
                LinkedSourceCaseId = targetCase.Id,
                SubcaseType = "LinkedSubcase",
                CreatedAt = DateTime.UtcNow
            };
            await StartSlaClockAsync(newLinkSubcase);
            await _caseRepository.AddAsync(newLinkSubcase);

            // 3. Save Sub-Case Relationship Entity
            var childRel = new CaseChildRelation
            {
                ChildId = childId,
                ParentCaseId = caseId,
                RelationType = ChildRelationType.Link,
                LinkedCaseId = targetCase.Id,
                Reason = !string.IsNullOrWhiteSpace(dto.RelationshipType) ? dto.RelationshipType : "Linked Case",
                CreatedByUserId = dto.UserId
            };
            await _caseRepository.AddChildRelationAsync(childRel);

            // 4. Save Legacy LinkedCase Record
            var linkedCase = new LinkedCase
            {
                CaseId = caseId,
                TargetCaseId = targetCase.Id
            };
            await _caseRepository.AddLinkedCaseAsync(linkedCase);
            
            // 5. Record Audit Event
            var caseEvent = new CaseEvent
            {
                CaseId = caseId,
                EventType = EventType.Note,
                Message = $"Linked case {targetCase.CaseNumber} (Sub-Case ID: {childId}, Reason: {dto.RelationshipType})",
                CreatedAt = DateTime.UtcNow,
                UserId = dto.UserId
            };
            await _caseRepository.AddEventAsync(caseEvent);
        });

            // Notification outside transaction — failure here is non-critical
            try
            {
                await _notificationService.CreateNotificationAsync(
                    existingCase.OwnerId,
                    "SUBCASE_CREATED",
                    "New Subcase Linked",
                    $"Subcase {childId} linked to Case {existingCase.CaseNumber}.",
                    newLinkSubcase.Id,
                    childId
                );
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Notification Trigger Error"); }

        return new LinkCaseResultDto
        {
            Message = "Case linked successfully.",
            ChildId = childId,
            NewCaseId = newLinkSubcase.Id,
            LinkedCaseNumber = targetCase.CaseNumber
        };
    }

    public async Task UnlinkCaseAsync(Guid caseId, UnlinkCaseDto dto)
    {
        var existingCase = await _caseRepository.GetByIdAsync(caseId);
        if (existingCase == null) throw new KeyNotFoundException("Case not found");
        
        var targetCase = await _caseRepository.GetByCaseNumberAsync(dto.TargetCaseNumber);
        Guid? targetCaseId = targetCase?.Id;
        string targetCaseNumber = dto.TargetCaseNumber;

        if (!targetCaseId.HasValue && existingCase.ChildRelations != null)
        {
            var matchingChildRel = existingCase.ChildRelations.FirstOrDefault(cr =>
                cr.RelationType == ChildRelationType.Link &&
                (cr.ChildId.Equals(dto.TargetCaseNumber, StringComparison.OrdinalIgnoreCase) ||
                 (cr.LinkedCase != null && cr.LinkedCase.CaseNumber.Equals(dto.TargetCaseNumber, StringComparison.OrdinalIgnoreCase))));

            if (matchingChildRel?.LinkedCaseId != null)
            {
                targetCaseId = matchingChildRel.LinkedCaseId.Value;
                if (matchingChildRel.LinkedCase != null)
                {
                    targetCaseNumber = matchingChildRel.LinkedCase.CaseNumber;
                }
            }
        }

        await ExecuteInTransactionAsync(async () =>
        {
            if (targetCaseId.HasValue)
            {
                await _caseRepository.RemoveLinkedCaseAsync(caseId, targetCaseId.Value);
            }
            else
            {
                // Remove by ChildId if matched
                var relationsToRemove = await _context.Set<CaseChildRelation>()
                    .Where(cr => (cr.ParentCaseId == caseId || cr.LinkedCaseId == caseId) &&
                                 cr.RelationType == ChildRelationType.Link &&
                                 cr.ChildId == dto.TargetCaseNumber)
                    .ToListAsync();

                if (relationsToRemove.Any())
                {
                    _context.Set<CaseChildRelation>().RemoveRange(relationsToRemove);
                    await _context.SaveChangesAsync();
                }
            }

            var caseEvent = new CaseEvent
            {
                CaseId = caseId,
                EventType = EventType.Note,
                Message = $"Unlinked from case {targetCaseNumber}",
                CreatedAt = DateTime.UtcNow,
                UserId = dto.UserId
            };
            await _caseRepository.AddEventAsync(caseEvent);
        });
    }

    public async Task<List<RelatedCustomerCaseDto>> GetRelatedCustomerCasesAsync(Guid caseId)
    {
        var existingCase = await _context.Cases
            .Include(c => c.LinkedCases)
            .Include(c => c.ChildRelations)
            .FirstOrDefaultAsync(c => c.Id == caseId);

        if (existingCase == null) return new List<RelatedCustomerCaseDto>();

        var customerCases = await _context.Cases
            .Where(c => c.CustomerId == existingCase.CustomerId && c.Id != caseId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        var linkedTargetIds = existingCase.LinkedCases.Select(lc => lc.TargetCaseId)
            .Concat(existingCase.ChildRelations.Where(cr => cr.LinkedCaseId.HasValue).Select(cr => cr.LinkedCaseId!.Value))
            .ToHashSet();

        return customerCases.Select(c => new RelatedCustomerCaseDto(
            c.Id,
            c.CaseNumber,
            c.CaseType,
            c.Title,
            c.Status.ToString(),
            c.Severity,
            c.CreatedAt,
            linkedTargetIds.Contains(c.Id)
        )).ToList();
    }

    public async Task ResolveCaseAsync(Guid caseId, ResolveCaseDto dto)
    {
        var existingCase = await _caseRepository.GetByIdAsync(caseId);
        if (existingCase == null) throw new KeyNotFoundException("Case not found");
        
        if (existingCase.Status == CaseStatus.Resolved)
            throw new InvalidOperationException("Case is already resolved.");
            
        var resolvedAt = DateTime.UtcNow;
        existingCase.Status = CaseStatus.Resolved;
        existingCase.ResolvedAt = resolvedAt;
        existingCase.Disposition = dto.Disposition;
        existingCase.ResolutionNote = dto.ResolutionNote;
        (await GetClockAsync()).Stop(existingCase, resolvedAt);

        var caseEvent = new CaseEvent
        {
            CaseId = caseId,
            EventType = EventType.Resolve,
            Message = $"Case resolved: {dto.Disposition}. {dto.ResolutionNote}",
            CreatedAt = DateTime.UtcNow,
            UserId = dto.UserId
        };

        await ExecuteInTransactionAsync(async () =>
        {
            await _caseRepository.UpdateAsync(existingCase);
            await _caseRepository.AddEventAsync(caseEvent);
        });

        try
        {
            await _notificationService.CreateNotificationAsync(
                existingCase.OwnerId,
                "CASE_RESOLVED",
                "Case Resolved",
                $"Case {existingCase.CaseNumber} ({existingCase.Title}) has been resolved. Disposition: {dto.Disposition}.",
                existingCase.Id,
                existingCase.CaseNumber
            );
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification Trigger Error"); }
    }

    public async Task<ReopenCaseResultDto> ReopenCaseAsync(Guid caseId, ReopenCaseDto dto)
    {
        var existingCase = await _caseRepository.GetByIdAsync(caseId);
        if (existingCase == null) throw new KeyNotFoundException("Case not found");
        
        if (existingCase.Status != CaseStatus.Resolved)
            throw new InvalidOperationException("Only resolved cases can be reopened.");

        string childId = string.Empty;
        Case newReopenSubcase = null!;

        await ExecuteInTransactionAsync(async () =>
        {
            // 1. Generate Sub-Case ID (e.g. C-00045-R01)
            int seq = await _caseRepository.GetNextChildSequenceAsync(caseId, ChildRelationType.Reopen);
            childId = $"{existingCase.CaseNumber}-R{seq:D2}";

            // 2. Create and Save NEW Child Subcase Record in Cases Table (Parent Case remains Resolved)
            newReopenSubcase = new Case
            {
                Id = Guid.NewGuid(),
                CaseNumber = childId,
                CaseType = existingCase.CaseType,
                Title = $"[Reopened] {existingCase.Title}",
                Description = $"Reopened from Parent Case {existingCase.CaseNumber}. Reason: {dto.Message}",
                Status = CaseStatus.Open,
                Severity = existingCase.Severity,
                DepartmentId = existingCase.DepartmentId,
                CustomerId = existingCase.CustomerId,
                OwnerId = existingCase.OwnerId,
                ParentCaseId = existingCase.Id,
                SubcaseType = "ReopenedSubcase",
                CreatedAt = DateTime.UtcNow
            };
            await StartSlaClockAsync(newReopenSubcase);
            await _caseRepository.AddAsync(newReopenSubcase);

            // 3. Save Sub-Case Relationship Entity
            var childRel = new CaseChildRelation
            {
                ChildId = childId,
                ParentCaseId = caseId,
                RelationType = ChildRelationType.Reopen,
                LinkedCaseId = null,
                Reason = dto.Message,
                CreatedByUserId = dto.UserId
            };
            await _caseRepository.AddChildRelationAsync(childRel);
            
            // Parent Case remains Closed/Resolved. Record audit note on Parent Case.
            var caseEvent = new CaseEvent
            {
                CaseId = caseId,
                EventType = EventType.Note,
                Message = $"Case reopened -> Created Sub-Case {childId} (Reason: {dto.Message})",
                CreatedAt = DateTime.UtcNow,
                UserId = dto.UserId
            };
            await _caseRepository.AddEventAsync(caseEvent);

            // Record audit event on new Child Subcase
            var subcaseEvent = new CaseEvent
            {
                CaseId = newReopenSubcase.Id,
                EventType = EventType.Create,
                Message = $"Reopened Sub-case created from Parent {existingCase.CaseNumber}.",
                CreatedAt = DateTime.UtcNow,
                UserId = dto.UserId
            };
            await _caseRepository.AddEventAsync(subcaseEvent);
        });

        // Notification outside transaction — failure here is non-critical
        try
        {
            await _notificationService.CreateNotificationAsync(
                newReopenSubcase.OwnerId,
                "CASE_REOPENED",
                "Subcase Reopened",
                $"Reopened Subcase {childId} created for Parent Case {existingCase.CaseNumber}.",
                newReopenSubcase.Id,
                childId
            );
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification Trigger Error"); }

        return new ReopenCaseResultDto
        {
            Message = "Subcase reopened successfully.",
            ChildId = childId,
            NewCaseId = newReopenSubcase.Id
        };
    }

    public async Task<PagedResponseDto<CaseAuditEventDto>> GetCaseAuditEventsAsync(int page = 1, int pageSize = 10, string? actionType = null, string? search = null, CancellationToken ct = default)
    {
        var events = await _caseRepository.GetCaseAuditEventsAsync(page, pageSize, actionType, search, ct);
        if (_piiMasking != null) await _piiMasking.MaskCasesAsync(events.Items, ct);   // audit rows show the customer's name
        return events;
    }

    public async Task AddTimelineInteractionAsync(Guid caseId, AddTimelineInteractionDto dto, Guid userId)
    {
        var existingCase = await _caseRepository.GetByIdAsync(caseId);
        if (existingCase == null) throw new KeyNotFoundException("Case not found");

        if (string.IsNullOrWhiteSpace(dto.Message))
            throw new ArgumentException("Message cannot be empty.");

        var sender = await _context.Users.FindAsync(userId);
        var channel = dto.IsInternal ? null : (dto.Channel ?? existingCase.SourceChannel ?? existingCase.CommunicationChannel ?? "Voice");

        var caseEvent = new CaseEvent
        {
            CaseId = caseId,
            EventType = dto.IsInternal ? EventType.Note : EventType.Comment,
            Message = dto.Message.Trim(),
            IsInternal = dto.IsInternal,
            Channel = channel,
            CreatedAt = DateTime.UtcNow,
            UserId = userId
        };
        await _caseRepository.AddEventAsync(caseEvent);

        if (!dto.IsInternal && !existingCase.FirstResponseActualAt.HasValue)
        {
            (await GetClockAsync()).RecordFirstResponse(existingCase, DateTime.UtcNow);
            await _caseRepository.UpdateAsync(existingCase);
        }

        // Internal notes notify the colleagues they @mention (one shared implementation).
        if (dto.IsInternal)
        {
            await _mentions.NotifyAsync(dto.Message, userId, sender?.Name, existingCase.Id, existingCase.CaseNumber, "Mentioned in Case");
        }
        else
        {
            // Customer Reply: notify case owner if different from replying agent
            if (existingCase.OwnerId != userId)
            {
                try
                {
                    await _notificationService.CreateNotificationAsync(
                        existingCase.OwnerId,
                        "CUSTOMER_REPLY_SENT",
                        "Reply Sent to Customer",
                        $"Reply sent to customer via {channel} on Case {existingCase.CaseNumber} by {sender?.Name ?? "Agent"}.",
                        existingCase.Id,
                        existingCase.CaseNumber,
                        "Medium"
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Customer Reply Notification Error");
                }
            }
        }
    }

    /// <summary>External SLA hours of a configured priority. A priority with no rule is a configuration error.</summary>
    /// <summary>Starts the SLA clock of a case created by a workflow (linked/reopened subcase) from its priority's configured rule.</summary>
    private async Task StartSlaClockAsync(Case c)
    {
        var rule = await GetPrioritySlaRuleAsync(c.Severity);
        (await GetClockAsync()).Start(c, rule, DateTime.UtcNow);
    }

    private async Task<PrioritySlaRule> GetPrioritySlaRuleAsync(string priority)
    {
        var lowered = priority.ToLower();
        return await _context.PrioritySlaRules.AsNoTracking().FirstOrDefaultAsync(r => r.Priority.ToLower() == lowered)
            ?? throw new InvalidOperationException($"No SLA rule is configured for priority '{priority}'. Configure it under Cases SLA & Routing.");
    }

    public async Task EscalateCaseAsync(Guid caseId, EscalateCaseDto dto, Guid userId)
    {
        var existingCase = await _context.Cases
            .Include(c => c.Department)
            .Include(c => c.Owner)
            .FirstOrDefaultAsync(c => c.Id == caseId);

        if (existingCase == null) throw new KeyNotFoundException("Case not found.");

        if (existingCase.Status == CaseStatus.Resolved || existingCase.Status == CaseStatus.Closed || existingCase.Status == CaseStatus.Cancelled)
            throw new InvalidOperationException($"Cannot escalate a case in '{existingCase.Status}' status.");

        // Escalation is its own action with its own reasons (ESCALATION_REASON), separate from assignment reasons.
        var reason = await CaseReasons.ResolveAsync(_context, CaseReasons.Escalation, dto.Reason, dto.ReasonDetail);

        var escalation = _escalation ?? throw new InvalidOperationException("Escalation is not configured.");
        var policy = await escalation.GetPolicyAsync();

        // The next ACTIVE level above the case's current one; none means the top of the matrix.
        var nextConfig = escalation.NextLevel(existingCase, policy);
        if (nextConfig == null)
        {
            var currentName = policy.ActiveLevels.FirstOrDefault(l => l.LevelNumber == existingCase.EscalationLevel)?.Name ?? $"Level {existingCase.EscalationLevel}";
            throw new InvalidOperationException($"Case has reached the maximum escalation level ({currentName}) and cannot be escalated further.");
        }
        int nextLevel = nextConfig.LevelNumber;

        User? targetUser;
        if (dto.TargetUserId.HasValue)
        {
            targetUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == dto.TargetUserId.Value && u.IsActive)
                ?? throw new ArgumentException("The chosen escalation target is not an active user.");
        }
        else
        {
            targetUser = await escalation.ResolveTargetAsync(existingCase, nextConfig)
                ?? throw new InvalidOperationException(
                    $"No active user can receive an escalation to {nextConfig.Name}. Assign someone the role '{nextConfig.TargetRole}' or choose a target person.");
        }

        var oldOwnerName = existingCase.Owner?.Name ?? "Agent";
        if (nextConfig.ReassignOwner)
        {
            existingCase.OwnerId = targetUser.Id;
        }

        // Escalating ends a pause: the case is now an active escalation, not waiting on the customer.
        (await GetClockAsync()).Resume(existingCase, DateTime.UtcNow);
        existingCase.EscalationLevel = nextLevel;
        existingCase.Status = CaseStatus.Escalated;

        var nextLevelName = nextConfig.Name;
        var targetRoleName = nextConfig.TargetRole;
        var reasonText = reason.Text;
        var noteText = !string.IsNullOrWhiteSpace(dto.Note) ? $" Note: {dto.Note.Trim()}" : "";
        var message = $"[Manual Escalation] Case escalated to {nextLevelName} ({targetRoleName}: {targetUser.Name}) from {oldOwnerName}. Reason: {reasonText}.{noteText}";

        var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);

        var caseEvent = new CaseEvent
        {
            CaseId = caseId,
            EventType = EventType.Escalate,
            Message = message,
            CreatedAt = DateTime.UtcNow,
            UserId = userId
        };

        await ExecuteInTransactionAsync(async () =>
        {
            await _context.SaveChangesAsync();
            await _caseRepository.AddEventAsync(caseEvent);
        });

        try
        {
            await _notificationService.CreateNotificationAsync(
                targetUser.Id,
                "CASE_ESCALATED",
                $"Case Escalated ({nextLevelName})",
                $"Case {existingCase.CaseNumber} has been escalated to you by {currentUser?.Name ?? "User"}. Reason: {reasonText}",
                existingCase.Id,
                existingCase.CaseNumber,
                "Critical"
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Escalate Notification Error");
        }
    }

    public async Task<EscalationMatrixResponseDto> GetEscalationMatrixConfigAsync(Guid? caseId = null, CancellationToken ct = default)
    {
        Case? c = null;
        if (caseId.HasValue)
        {
            c = await _context.Cases
                .Include(x => x.Department)
                .Include(x => x.Owner)
                .FirstOrDefaultAsync(x => x.Id == caseId.Value, ct);
        }

        var escalation = _escalation ?? throw new InvalidOperationException("Escalation is not configured.");
        var policy = await escalation.GetPolicyAsync(ct);
        var levels = policy.ActiveLevels;

        int currentLevel = c != null && c.EscalationLevel > 0 ? c.EscalationLevel : 1;
        var nextConfig = c != null ? escalation.NextLevel(c, policy) : null;
        int? nextLevel = nextConfig?.LevelNumber;
        User? nextUser = nextConfig != null && c != null ? await escalation.ResolveTargetAsync(c, nextConfig, ct) : null;

        var configuredUserIds = levels.Where(l => l.TargetUserId.HasValue).Select(l => l.TargetUserId!.Value).Distinct().ToList();
        var configuredUsers = await _context.Users.AsNoTracking().Where(u => configuredUserIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        var levelDtos = new List<EscalationMatrixLevelDto>();
        foreach (var lvl in levels)
        {
            User? targetUser = c != null
                ? await escalation.ResolveTargetAsync(c, lvl, ct)
                : (lvl.TargetUserId.HasValue ? configuredUsers.GetValueOrDefault(lvl.TargetUserId.Value) : null);

            levelDtos.Add(new EscalationMatrixLevelDto
            {
                Level = lvl.LevelNumber,
                Name = lvl.Name,
                Role = lvl.TargetRole,
                Trigger = EscalationTriggers.Describe(lvl.TriggerType, lvl.TriggerValue),
                Action = lvl.ActionDescription,
                CurrentTargetUserName = targetUser?.Name,
                CurrentTargetUserId = targetUser?.Id
            });
        }

        return new EscalationMatrixResponseDto
        {
            Title = "Escalation Matrix",
            Subtitle = "Sequential escalation architecture with dynamic database configuration, audit logs, and automatic/manual routing.",
            CurrentLevel = currentLevel,
            NextLevel = nextLevel,
            NextTargetRole = nextConfig?.TargetRole,
            NextTargetUserName = nextUser?.Name,
            NextTargetUserId = nextUser?.Id,
            Levels = levelDtos
        };
    }

    // ======================== STATUS TRANSITION VALIDATION ========================

    /// <summary>
    /// Defines the approved status transitions. Resolved is only reachable through
    /// ResolveCaseAsync. Escalated is only reachable through EscalateCaseAsync.
    /// </summary>
    private static readonly Dictionary<CaseStatus, CaseStatus[]> AllowedTransitions = new()
    {
        [CaseStatus.Open] = new[] { CaseStatus.InProgress, CaseStatus.WaitingOnCustomer },
        [CaseStatus.InProgress] = new[] { CaseStatus.Open, CaseStatus.WaitingOnCustomer },
        [CaseStatus.WaitingOnCustomer] = new[] { CaseStatus.Open, CaseStatus.InProgress },
        [CaseStatus.Escalated] = new[] { CaseStatus.Open, CaseStatus.InProgress, CaseStatus.WaitingOnCustomer },
        // Resolved and Closed are terminal via their dedicated workflows
        [CaseStatus.Resolved] = Array.Empty<CaseStatus>(),
        [CaseStatus.Closed] = Array.Empty<CaseStatus>(),
        [CaseStatus.Cancelled] = Array.Empty<CaseStatus>(),
    };

    public static bool IsValidTransition(CaseStatus from, CaseStatus to)
    {
        if (AllowedTransitions.TryGetValue(from, out var allowed))
            return allowed.Contains(to);
        return false;
    }

    public static IEnumerable<string> GetAllowedTransitions(CaseStatus from)
    {
        if (AllowedTransitions.TryGetValue(from, out var allowed))
            return allowed.Select(s => s.ToString());
        return Enumerable.Empty<string>();
    }

    // ======================== DASHBOARD SUMMARY ========================

    // ======================== TIMELINE PAGINATION ========================

    public async Task<PagedResponseDto<CaseEventDto>> GetCaseTimelineEventsAsync(
        Guid caseId, DateTime? before, int limit = 50, CancellationToken ct = default)
    {
        var query = _context.CaseEvents
            .AsNoTracking()
            .Include(e => e.User)
            .Where(e => e.CaseId == caseId);

        if (before.HasValue)
            query = query.Where(e => e.CreatedAt < before.Value);

        var totalCount = await query.CountAsync(ct);

        var events = await query
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .Select(e => new CaseEventDto
            {
                Id = e.Id,
                UserId = e.UserId,
                EventType = e.EventType.ToString(),
                Message = e.Message,
                CreatedAt = e.CreatedAt,
                UserName = e.User != null ? e.User.Name : string.Empty,
                IsInternal = e.IsInternal,
                Channel = e.Channel
            })
            .ToListAsync(ct);

        return new PagedResponseDto<CaseEventDto>
        {
            Items = events,
            TotalCount = totalCount,
            Page = 1,
            PageSize = limit
        };
    }

    /// <summary>Validates a create-case request without creating anything.</summary>
    public async Task ValidateCreateCaseMetadataAsync(CreateCaseDto dto, CancellationToken ct = default)
        => await ValidateCreateCaseAsync(dto, ct);

    /// <summary>A create-case request after validation: resolved records and canonical values.</summary>
    private sealed record ValidatedCase(
        Department Department,
        CaseTypeConfig CaseType,
        DepartmentSubCategory SubCategory,
        IReadOnlyDictionary<string, string> Values,
        IReadOnlyDictionary<string, string> CustomAttributes)
    {
        public string Value(string field) => Values.TryGetValue(field, out var v) ? v : string.Empty;
    }

    /// <summary>Setup data that changes rarely comes from the configuration cache, which is cleared as soon as any setting is saved.</summary>
    private Task<List<T>> CachedAsync<T>(string key, Func<Task<List<T>>> load) => _configCache == null ? load() : _configCache.GetOrCreateAsync(key, load);

    private async Task<ValidatedCase> ValidateCreateCaseAsync(CreateCaseDto dto, CancellationToken ct = default)
    {
        var engine = _fieldValidation ?? throw new InvalidOperationException("Field validation is not available.");
        var errors = new List<FieldError>();

        // 1. Structural checks: the records the case points at must exist and be usable.
        // A case cannot exist without these, whatever the field configuration says (they are system-required).
        Guid? customerId = null;
        if (dto.CustomerId == Guid.Empty)
            errors.Add(new FieldError("selectCustomer", "A valid customer must be selected."));
        else if (await _context.Customers.AnyAsync(c => c.Id == dto.CustomerId, ct)) customerId = dto.CustomerId;
        else errors.Add(new FieldError("selectCustomer", "Selected customer does not exist."));

        Department? department = null;
        if (dto.DepartmentId == Guid.Empty)
        {
            errors.Add(new FieldError("departmentId", "Department is required."));
        }
        else
        {
            department = (await CachedAsync("validation:departments", () => _context.Departments.AsNoTracking().ToListAsync(ct)))
                .FirstOrDefault(d => d.Id == dto.DepartmentId);
            if (department == null || !department.IsActive)
            {
                errors.Add(new FieldError("departmentId", "Selected department is invalid or inactive."));
                department = null;
            }
        }

        CaseTypeConfig? caseType = null;
        var caseTypeInput = dto.CaseType?.Trim() ?? string.Empty;
        if (caseTypeInput.Length == 0)
        {
            errors.Add(new FieldError("caseType", "Case type is required."));
        }
        else
        {
            var lowered = caseTypeInput.ToLowerInvariant();
            caseType = (await CachedAsync("validation:case-types", () => _context.CaseTypeConfigs.AsNoTracking().ToListAsync(ct)))
                .FirstOrDefault(c => c.IsActive && (c.Code.ToLowerInvariant() == lowered || c.Name.ToLowerInvariant() == lowered));
            if (caseType == null)
                errors.Add(new FieldError("caseType", $"Case type '{caseTypeInput}' is not configured or is inactive."));
        }

        DepartmentSubCategory? subCategory = null;
        var subCategoryInput = dto.Subcategory?.Trim() ?? string.Empty;
        if (subCategoryInput.Length == 0)
        {
            errors.Add(new FieldError("subCategory", "Sub-category is required."));
        }
        else if (department != null)
        {
            var lowered = subCategoryInput.ToLowerInvariant();
            subCategory = (await CachedAsync("validation:sub-categories", () => _context.DepartmentSubCategories.AsNoTracking().ToListAsync(ct)))
                .FirstOrDefault(s => s.DepartmentId == department.Id && s.IsActive && s.Name.ToLowerInvariant() == lowered);
            if (subCategory == null)
                errors.Add(new FieldError("subCategory", $"Sub-category '{subCategoryInput}' is invalid for department '{department.Name}'."));
        }

        // 2. Configuration-driven checks: required, lengths, patterns, field types, dropdown options, custom fields.
        var preferred = dto.PreferredCommunicationChannel;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["caseType"] = caseType?.Name,
            ["title"] = dto.Title,
            ["description"] = dto.Description,
            ["selectCustomer"] = customerId?.ToString(),
            ["departmentId"] = department?.Id.ToString(),
            ["subCategory"] = subCategory?.Name,
            ["preferredLanguage"] = dto.PreferredLanguage,
            ["preferredCommunicationChannel"] = preferred,
            ["communicationChannel"] = preferred,                                   // legacy key for the same field
            ["sourceChannel"] = !string.IsNullOrWhiteSpace(dto.SourceChannel) ? dto.SourceChannel : dto.CommunicationChannel,
            ["severity"] = dto.Severity,
        };
        foreach (var (key, value) in dto.CustomAttributes ?? new Dictionary<string, string>())
            values[key] = value;

        // Fields whose problem was already reported structurally are not reported a second time as "required".
        var alreadyFailed = errors.Select(e => e.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = await engine.ValidateAsync(
            "CaseManagement", "CreateCase", values,
            customFieldKeys: dto.CustomAttributes?.Keys,
            // The priority is mandatory only when the sub-category has no configured one; the priority resolver
            // owns that rule (and its error message).
            requirednessHandledElsewhere: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "severity" },
            ct);
        errors.AddRange(result.Errors.Where(e => !alreadyFailed.Contains(e.Field)));

        if (errors.Count > 0) throw new FieldValidationException(errors);

        var custom = result.Normalized
            .Where(kv => (dto.CustomAttributes?.ContainsKey(kv.Key) ?? false) && kv.Value.Length > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        return new ValidatedCase(department!, caseType!, subCategory!, result.Normalized, custom);
    }
}
