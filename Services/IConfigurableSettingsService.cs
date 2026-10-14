namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;

public interface IConfigurableSettingsService
{
    Task<IEnumerable<FieldConfigurationDto>> GetFieldConfigurationsAsync(string moduleKey, string? sectionKey = null, CancellationToken ct = default);
    Task<IReadOnlyList<FieldConfigurationDto>> SaveFieldConfigurationsAsync(UpdateFieldConfigurationsRequest request, CancellationToken ct = default);
    Task<FieldConfigurationDto> AddCustomFieldAsync(CreateCustomFieldDto dto, CancellationToken ct = default);
    Task<FieldConfigurationDto?> UpdateFieldConfigurationAsync(Guid id, UpdateFieldConfigurationDto dto, CancellationToken ct = default);
    Task<bool> DeleteFieldConfigurationAsync(Guid id, CancellationToken ct = default);
    Task<TypeChangeResult?> CheckTypeChangeAsync(Guid id, FieldConfigurationDto proposal, CancellationToken ct = default);

    Task<IEnumerable<LookupTypeDto>> GetLookupTypesAsync(CancellationToken ct = default);
    Task<IEnumerable<LookupValueDto>> GetLookupValuesAsync(string typeCode, bool activeOnly = true, CancellationToken ct = default);
    Task<LookupValueDto> AddLookupValueAsync(CreateLookupValueDto dto, CancellationToken ct = default);
    Task<LookupValueDto?> UpdateLookupValueAsync(Guid id, UpdateLookupValueDto dto, CancellationToken ct = default);
    Task<bool> DeleteLookupValueAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<LookupValueDto>> SaveLookupValuesAsync(string typeCode, SaveLookupValuesRequest request, CancellationToken ct = default);

    // Case Management Settings
    Task<IEnumerable<CaseTypeConfigDto>> GetCaseTypesAsync(bool activeOnly = true, CancellationToken ct = default);
    Task<CaseTypeConfigDto> AddCaseTypeAsync(CreateCaseTypeConfigDto dto, CancellationToken ct = default);
    Task<CaseTypeConfigDto?> UpdateCaseTypeAsync(Guid id, UpdateCaseTypeConfigDto dto, CancellationToken ct = default);
    Task<bool> DeleteCaseTypeAsync(Guid id, CancellationToken ct = default);

    Task<IEnumerable<DepartmentSubCategoryDto>> GetSubCategoriesAsync(Guid? departmentId = null, bool activeOnly = true, CancellationToken ct = default);
    Task<DepartmentSubCategoryDto> AddSubCategoryAsync(CreateDepartmentSubCategoryDto dto, CancellationToken ct = default);
    Task<DepartmentSubCategoryDto?> UpdateSubCategoryAsync(Guid id, UpdateDepartmentSubCategoryDto dto, CancellationToken ct = default);
    Task<bool> DeleteSubCategoryAsync(Guid id, CancellationToken ct = default);

    // Priority ("severity") master data. Priorities live in PrioritySlaRules — the same rows the
    // Cases SLA & Routing screen edits — so there is exactly one list and one set of SLA targets.
    Task<IEnumerable<SeverityDto>> GetSeverityConfigurationsAsync(CancellationToken ct = default);
    Task<SeverityDto> AddSeverityAsync(CreateSeverityDto dto, CancellationToken ct = default);
    Task<SeverityDto?> UpdateSeverityAsync(Guid id, UpdateSeverityDto dto, CancellationToken ct = default);
    Task<bool> DeleteSeverityAsync(Guid id, CancellationToken ct = default);

}
