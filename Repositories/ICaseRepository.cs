namespace CaseManagement.Api.Repositories;

using CaseManagement.Api.Models;
using CaseManagement.Api.DTOs;

public interface ICaseRepository
{
    Task<Case?> GetByIdAsync(Guid id);
    Task<IEnumerable<Case>> GetAllAsync(Guid? departmentId = null);
    Task<Case> AddAsync(Case newCase);
    Task UpdateAsync(Case caseToUpdate);
    Task AddEventAsync(CaseEvent caseEvent);
    Task AddParticipantAsync(CaseParticipant participant);
    Task RemoveParticipantAsync(Guid caseId, Guid userId);
    Task<Case?> GetByCaseNumberAsync(string caseNumber);
    Task AddLinkedCaseAsync(LinkedCase linkedCase);
    Task RemoveLinkedCaseAsync(Guid caseId, Guid targetCaseId);
    Task<PagedResponseDto<CaseSummaryDto>> GetPaginatedBoardCasesAsync(string? status = null, int page = 1, int pageSize = 30, Guid? departmentId = null, string? caseType = null, string? search = null, string? priority = null, string? channel = null, CancellationToken ct = default);
    Task<CaseDetailDto?> GetCaseDetailAsync(Guid id, CancellationToken ct = default);

    /// <summary>Header smart-search over cases, capped at <paramref name="limit"/> hits.</summary>
    Task<IEnumerable<SearchCaseHitDto>> SearchCasesAsync(string query, int limit, CancellationToken ct = default);
    Task<Guid?> GetDepartmentOwnerAsync(Guid departmentId);
    /// <summary>Atomically allocates the next top-level case sequence number.</summary>
    Task<int> GetNextCaseSequenceAsync(CancellationToken ct = default);
    
    // Sub-Case Child Relations
    Task<int> GetNextChildSequenceAsync(Guid parentCaseId, ChildRelationType relationType, CancellationToken ct = default);
    Task AddChildRelationAsync(CaseChildRelation relation, CancellationToken ct = default);
    Task<CaseChildRelation?> GetChildRelationByChildIdAsync(string childId, CancellationToken ct = default);

    // Case Audit Events
    Task<PagedResponseDto<CaseAuditEventDto>> GetCaseAuditEventsAsync(int page = 1, int pageSize = 10, string? actionType = null, string? search = null, CancellationToken ct = default);
}
