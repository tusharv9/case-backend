namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

public interface ISlaRoutingService
{
    Task<SlaRoutingConfigResponseDto> GetFullConfigurationAsync(CancellationToken ct = default);
    Task<bool> UpdateConfigurationAsync(UpdateSlaRoutingConfigRequestDto request, Guid actingUserId, CancellationToken ct = default);

    Task<PublicHolidayDto> AddPublicHolidayAsync(CreatePublicHolidayDto dto, Guid actingUserId, CancellationToken ct = default);
    Task<PublicHolidayDto?> UpdatePublicHolidayAsync(Guid id, UpdatePublicHolidayDto dto, Guid actingUserId, CancellationToken ct = default);
    Task<bool> DeletePublicHolidayAsync(Guid id, Guid actingUserId, CancellationToken ct = default);

    Task<EscalationLevelConfigDto> AddEscalationLevelAsync(CreateEscalationLevelDto dto, Guid actingUserId, CancellationToken ct = default);
    Task<EscalationLevelConfigDto?> UpdateEscalationLevelAsync(Guid id, UpdateEscalationLevelDto dto, Guid actingUserId, CancellationToken ct = default);
    Task<bool> DeleteEscalationLevelAsync(Guid id, Guid actingUserId, CancellationToken ct = default);

    Task<PriorityResolution> ResolveEffectivePriorityAsync(Guid? departmentId, string? subCategoryName, string? requestedPriority, CancellationToken ct = default);
    Task<PriorityResolutionDto> PreviewPriorityAsync(Guid? departmentId, string? subCategoryName, CancellationToken ct = default);
    Task<PrioritySlaRule> GetActivePrioritySlaRuleAsync(string priority, CancellationToken ct = default);
    Task<CaseEscalationStatusDto?> GetCaseEscalationStatusAsync(Guid caseId, CancellationToken ct = default);
}

/// <summary>The priority a case gets and why: the sub-category's configured mapping wins; otherwise the requested one.</summary>
public sealed record PriorityResolution(string Priority, string Source, Guid? SubCategoryId);
