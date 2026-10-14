namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;

public interface ICaseService
{
    Task<Case> CreateCaseAsync(CreateCaseDto dto, Guid createdByUserId);
    Task<CaseDetailDto?> GetCaseDetailsAsync(Guid caseId, CancellationToken ct = default);
    Task<IEnumerable<SearchCaseHitDto>> SearchCasesAsync(string query, int limit, CancellationToken ct = default);
    Task<PagedResponseDto<CaseSummaryDto>> GetPaginatedBoardCasesAsync(string? status = null, int page = 1, int pageSize = 30, Guid? departmentId = null, string? caseType = null, string? search = null, string? priority = null, string? channel = null, CancellationToken ct = default);
    Task UpdateCaseStatusAsync(Guid caseId, UpdateCaseStatusDto dto);
    /// <summary>Hands the case to another person after checking the caller may do so and the person is eligible (403 / 422 otherwise).</summary>
    Task AssignCaseAsync(Guid caseId, AssignCaseDto dto, bool callerMayAssignAnyCase = false);
    Task AddNoteAsync(Guid caseId, AddNoteDto dto);
    Task TransferDepartmentAsync(Guid caseId, TransferDepartmentDto dto, Guid transferredByUserId, bool callerMayAssignAnyCase = false);

    /// <summary>Everyone who could hold the case under the manual-assignment policy, with live status, workload and why not.</summary>
    Task<EligibleAssigneesDto> GetEligibleAssigneesAsync(Guid caseId, bool callerMayAssignAnyCase = false, CancellationToken ct = default);
    Task<List<RelatedCustomerCaseDto>> GetRelatedCustomerCasesAsync(Guid caseId);
    Task<LinkCaseResultDto> LinkCaseAsync(Guid caseId, LinkCaseDto dto);

    Task UnlinkCaseAsync(Guid caseId, UnlinkCaseDto dto);
    Task ResolveCaseAsync(Guid caseId, ResolveCaseDto dto);
    Task EscalateCaseAsync(Guid caseId, EscalateCaseDto dto, Guid userId);
    
    Task<EscalationMatrixResponseDto> GetEscalationMatrixConfigAsync(Guid? caseId = null, CancellationToken ct = default);
    Task<ReopenCaseResultDto> ReopenCaseAsync(Guid caseId, ReopenCaseDto dto);
    Task<PagedResponseDto<CaseAuditEventDto>> GetCaseAuditEventsAsync(int page = 1, int pageSize = 10, string? actionType = null, string? search = null, CancellationToken ct = default);
    Task AddTimelineInteractionAsync(Guid caseId, AddTimelineInteractionDto dto, Guid userId);


    // Header counts for the Case Management page
    Task<CaseStatsDto> GetCaseStatsAsync(Guid? departmentId, CancellationToken ct = default);

    // Paginated case detail timeline
    Task<PagedResponseDto<CaseEventDto>> GetCaseTimelineEventsAsync(
        Guid caseId, DateTime? before, int limit = 50, CancellationToken ct = default);
}
