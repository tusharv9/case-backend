namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;

public interface ICaseService
{
    /// <param name="withinTransaction">Optional work saved in the same transaction as the new case (it commits or rolls back with
    /// it) — e.g. email intake recording which email the case came from, so a case can never exist without that link. It may run
    /// more than once when the database connection is retried, so it must be safe to repeat.</param>
    Task<Case> CreateCaseAsync(CreateCaseDto dto, Guid createdByUserId, Func<Case, Task>? withinTransaction = null, bool callerMayAssignAnyCase = false);

    /// <summary>Create Case → Assign to: who in the department's team could take a new case, and why anyone cannot.</summary>
    Task<List<AssigneeOptionDto>> GetDepartmentAssigneesAsync(Guid departmentId, CancellationToken ct = default);

    /// <summary>How a new case of the department and source channel would be given out (team, algorithm, agent required).</summary>
    Task<AssignmentRuleDto?> GetAssignmentRuleAsync(Guid departmentId, string? sourceChannel, CancellationToken ct = default);

    /// <summary>
    /// <see cref="CreateCaseAsync"/> for a ticket being assigned: <paramref name="ownerId"/> (when given) is the person chosen
    /// instead of automatic routing — judged by the same Assign &amp; Reassign rules as a manual assignment, cross-team only with
    /// <paramref name="callerMayAssignAnyCase"/>. The case's SLA starts now, when the case is made, like any case: the time the
    /// ticket waited is the queue's (the ticket SLA), never the team's. <paramref name="origin"/>'s arrival is kept on the case as
    /// the request time, so the customer's whole wait stays visible.
    /// </summary>
    Task<Case> CreateCaseForTicketAsync(CreateCaseDto dto, Guid createdByUserId, Guid? ownerId, TicketOrigin origin, bool callerMayAssignAnyCase,
        Func<Case, Task>? withinTransaction = null);
    Task<CaseDetailDto?> GetCaseDetailsAsync(Guid caseId, CancellationToken ct = default);
    Task<IEnumerable<SearchCaseHitDto>> SearchCasesAsync(string query, int limit, CancellationToken ct = default);
    /// <summary>Every case of one customer (local or Customer 360), newest first, masked.</summary>
    Task<PagedResponseDto<CaseSummaryDto>> GetCasesForCustomerAsync(CustomerCasesQueryDto query, CancellationToken ct = default);
    Task<PagedResponseDto<CaseSummaryDto>> GetPaginatedBoardCasesAsync(string? status = null, int page = 1, int pageSize = 30, Guid? departmentId = null, string? caseType = null, string? search = null, string? priority = null, string? channel = null, CancellationToken ct = default);
    Task UpdateCaseStatusAsync(Guid caseId, UpdateCaseStatusDto dto);
    /// <summary>Hands the case to another person after checking the caller may do so and the person is eligible (403 / 422 otherwise).</summary>
    Task AssignCaseAsync(Guid caseId, AssignCaseDto dto, bool callerMayAssignAnyCase = false);
    Task AddNoteAsync(Guid caseId, AddNoteDto dto);
    /// <summary>Open → In Progress on the first real work done on the case (see CaseService.StartWorkIfOpenAsync). True when it moved.</summary>
    Task<bool> MarkWorkStartedAsync(Guid caseId, Guid userId, string action);
    Task TransferDepartmentAsync(Guid caseId, TransferDepartmentDto dto, Guid transferredByUserId, bool callerMayAssignAnyCase = false);

    /// <summary>Everyone who could hold the case under the manual-assignment policy, with live status, workload and why not.</summary>
    Task<EligibleAssigneesDto> GetEligibleAssigneesAsync(Guid caseId, bool callerMayAssignAnyCase = false, CancellationToken ct = default);
    Task<List<RelatedCustomerCaseDto>> GetRelatedCustomerCasesAsync(Guid caseId);
    Task<LinkCaseResultDto> LinkCaseAsync(Guid caseId, LinkCaseDto dto);

    Task UnlinkCaseAsync(Guid caseId, UnlinkCaseDto dto);
    Task ResolveCaseAsync(Guid caseId, ResolveCaseDto dto);
    Task EscalateCaseAsync(Guid caseId, EscalateCaseDto dto, Guid userId);
    Task<EscalationOptionsDto> GetEscalationOptionsAsync(Guid caseId, Guid userId, CancellationToken ct = default);
    /// <summary>Resolve with the single-case rules; <paramref name="bulk"/> only labels the audit event.</summary>
    Task ResolveCaseAsync(Guid caseId, ResolveCaseDto dto, bool bulk);
    
    Task<EscalationMatrixResponseDto> GetEscalationMatrixConfigAsync(Guid? caseId = null, CancellationToken ct = default);
    Task<ReopenCaseResultDto> ReopenCaseAsync(Guid caseId, ReopenCaseDto dto);
    Task<PagedResponseDto<CaseAuditEventDto>> GetCaseAuditEventsAsync(AuditFilter filter, int page = 1, int pageSize = 10, CancellationToken ct = default);
    Task AddTimelineInteractionAsync(Guid caseId, AddTimelineInteractionDto dto, Guid userId);


    // Header counts for the Case Management page
    Task<CaseStatsDto> GetCaseStatsAsync(Guid? departmentId, CancellationToken ct = default);

    // Paginated case detail timeline
    Task<PagedResponseDto<CaseEventDto>> GetCaseTimelineEventsAsync(
        Guid caseId, DateTime? before, int limit = 50, CancellationToken ct = default);
}

/// <summary>The ticket a case is being made from: which one, its number, and when the customer's request arrived.</summary>
public sealed record TicketOrigin(Guid TicketId, string TicketNumber, DateTime ReceivedAt);
