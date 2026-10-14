namespace CaseManagement.Api.Repositories;

using CaseManagement.Api.Models;

public interface IConfigurableSettingsRepository
{
    Task<IEnumerable<FieldConfiguration>> GetFieldConfigurationsAsync(string moduleKey, string? sectionKey = null, CancellationToken ct = default);
    /// <summary>Applies the updates (matched by Id) and inserts the creates in ONE transaction.</summary>
    Task SaveFieldConfigurationsAsync(string moduleKey, string sectionKey, IEnumerable<FieldConfiguration> updates, IEnumerable<FieldConfiguration> creates, CancellationToken ct = default);
    Task<FieldConfiguration?> GetFieldConfigurationAsync(Guid id, CancellationToken ct = default);
    Task<FieldConfiguration> AddFieldConfigurationAsync(FieldConfiguration field, CancellationToken ct = default);
    Task<FieldConfiguration?> UpdateFieldConfigurationAsync(Guid id, FieldConfiguration field, CancellationToken ct = default);
    Task<bool> DeleteFieldConfigurationAsync(Guid id, CancellationToken ct = default);
    Task<bool> FieldExistsAsync(string moduleKey, string sectionKey, string apiField, Guid? excludeId = null, CancellationToken ct = default);
    
    Task<IEnumerable<LookupType>> GetAllLookupTypesAsync(CancellationToken ct = default);
    Task<IEnumerable<LookupValue>> GetLookupValuesAsync(string typeCode, bool activeOnly = true, CancellationToken ct = default);
    Task<LookupValue> AddLookupValueAsync(LookupValue value, CancellationToken ct = default);
    Task<LookupValue?> UpdateLookupValueAsync(Guid id, LookupValue value, CancellationToken ct = default);
    Task<bool> DeleteLookupValueAsync(Guid id, CancellationToken ct = default);
    Task<LookupValue?> GetLookupValueAsync(Guid id, CancellationToken ct = default);
    Task<bool> LookupValueExistsAsync(string typeCode, string value, Guid? excludeId = null, CancellationToken ct = default);

    // Case Management Settings
    Task<IEnumerable<CaseTypeConfig>> GetCaseTypesAsync(bool activeOnly = true, CancellationToken ct = default);
    Task<CaseTypeConfig> AddCaseTypeAsync(CaseTypeConfig config, CancellationToken ct = default);
    Task<CaseTypeConfig?> UpdateCaseTypeAsync(Guid id, CaseTypeConfig config, CancellationToken ct = default);
    Task<bool> DeleteCaseTypeAsync(Guid id, CancellationToken ct = default);
    Task<bool> CaseTypeExistsAsync(string code, string name, Guid? excludeId = null, CancellationToken ct = default);

    Task<IEnumerable<DepartmentSubCategory>> GetSubCategoriesAsync(Guid? departmentId = null, bool activeOnly = true, CancellationToken ct = default);
    Task<DepartmentSubCategory> AddSubCategoryAsync(DepartmentSubCategory subCategory, CancellationToken ct = default);
    Task<DepartmentSubCategory?> UpdateSubCategoryAsync(Guid id, DepartmentSubCategory subCategory, CancellationToken ct = default);
    Task<bool> DeleteSubCategoryAsync(Guid id, CancellationToken ct = default);
    Task<bool> SubCategoryExistsAsync(Guid departmentId, string name, Guid? excludeId = null, CancellationToken ct = default);
    Task<bool> DepartmentExistsAsync(Guid departmentId, CancellationToken ct = default);

    Task<int> CountCasesBySeverityAsync(string severity, CancellationToken ct = default);
    Task<int> CountCasesByCaseTypeAsync(string code, string name, CancellationToken ct = default);
    Task<int> CountCasesBySubCategoryAsync(Guid departmentId, string name, CancellationToken ct = default);
    Task<int> RenameCaseSeverityAsync(string oldSeverity, string newSeverity, CancellationToken ct = default);

}
