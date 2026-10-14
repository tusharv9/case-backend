namespace CaseManagement.Api.Repositories;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public class ConfigurableSettingsRepository : IConfigurableSettingsRepository
{
    private readonly AppDbContext _context;

    public ConfigurableSettingsRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<FieldConfiguration>> GetFieldConfigurationsAsync(string moduleKey, string? sectionKey = null, CancellationToken ct = default)
    {
        var query = _context.FieldConfigurations.AsNoTracking().Where(f => f.ModuleKey == moduleKey);
        if (!string.IsNullOrWhiteSpace(sectionKey))
        {
            query = query.Where(f => f.SectionKey == sectionKey);
        }
        return await query.OrderBy(f => f.DisplayOrder).ToListAsync(ct);
    }

    public async Task SaveFieldConfigurationsAsync(string moduleKey, string sectionKey, IEnumerable<FieldConfiguration> updates, IEnumerable<FieldConfiguration> creates, CancellationToken ct = default)
    {
        var existing = await _context.FieldConfigurations
            .Where(f => f.ModuleKey == moduleKey && f.SectionKey == sectionKey)
            .ToListAsync(ct);

        foreach (var updated in updates)
        {
            var target = existing.FirstOrDefault(e => e.Id == updated.Id);
            if (target == null) continue;
            CopyDefinition(target, updated);
            ApplySystemLock(target);
            target.UpdatedAt = DateTime.UtcNow;
        }

        foreach (var created in creates)
        {
            created.Id = created.Id == Guid.Empty ? Guid.NewGuid() : created.Id;
            created.ModuleKey = moduleKey;
            created.SectionKey = sectionKey;
            created.CreatedAt = DateTime.UtcNow;
            created.IsSystemRequired = false;   // only the system defines these; a client cannot create a locked field
            _context.FieldConfigurations.Add(created);
        }

        // One SaveChanges = one transaction: every change is stored, or none is.
        await _context.SaveChangesAsync(ct);
    }

    private static void CopyDefinition(FieldConfiguration target, FieldConfiguration source)
    {
        target.DisplayLabel = source.DisplayLabel;
        target.FieldType = source.FieldType;
        target.IsVisible = source.IsVisible;
        target.IsRequired = source.IsRequired;
        target.MaskingRule = source.MaskingRule;
        target.VisibleChars = source.VisibleChars;
        target.DisplayOrder = source.DisplayOrder;
        target.LookupTypeCode = source.LookupTypeCode;
        target.ValidationRegex = source.ValidationRegex;
        target.ValidationMessage = source.ValidationMessage;
        target.MinLength = source.MinLength;
        target.MaxLength = source.MaxLength;
        target.MinValue = source.MinValue;
        target.MaxValue = source.MaxValue;
    }

    /// <summary>A system-required field is always required and visible, whatever a client sent.</summary>
    private static void ApplySystemLock(FieldConfiguration field)
    {
        if (!field.IsSystemRequired) return;
        field.IsRequired = true;
        field.IsVisible = true;
    }

    public async Task<FieldConfiguration?> GetFieldConfigurationAsync(Guid id, CancellationToken ct = default) =>
        await _context.FieldConfigurations.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);

    public async Task<FieldConfiguration> AddFieldConfigurationAsync(FieldConfiguration field, CancellationToken ct = default)
    {
        field.Id = Guid.NewGuid();
        field.CreatedAt = DateTime.UtcNow;
        _context.FieldConfigurations.Add(field);
        await _context.SaveChangesAsync(ct);
        return field;
    }

    public async Task<FieldConfiguration?> UpdateFieldConfigurationAsync(Guid id, FieldConfiguration updated, CancellationToken ct = default)
    {
        var target = await _context.FieldConfigurations.FindAsync(new object[] { id }, ct);
        if (target == null) return null;

        CopyDefinition(target, updated);
        ApplySystemLock(target);
        target.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return target;
    }

    public async Task<bool> FieldExistsAsync(string moduleKey, string sectionKey, string apiField, Guid? excludeId = null, CancellationToken ct = default)
    {
        return await _context.FieldConfigurations.AnyAsync(f =>
            f.ModuleKey == moduleKey &&
            f.SectionKey == sectionKey &&
            f.ApiField.ToLower() == apiField.ToLower() &&
            (excludeId == null || f.Id != excludeId), ct);
    }

    public async Task<bool> DeleteFieldConfigurationAsync(Guid id, CancellationToken ct = default)
    {
        var target = await _context.FieldConfigurations.FindAsync(new object[] { id }, ct);
        if (target == null) return false;

        _context.FieldConfigurations.Remove(target);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IEnumerable<LookupType>> GetAllLookupTypesAsync(CancellationToken ct = default)
    {
        return await _context.LookupTypes
            .Include(lt => lt.Values.OrderBy(v => v.DisplayOrder))
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<IEnumerable<LookupValue>> GetLookupValuesAsync(string typeCode, bool activeOnly = true, CancellationToken ct = default)
    {
        var query = _context.LookupValues.AsNoTracking().Where(v => v.TypeCode == typeCode);
        if (activeOnly) query = query.Where(v => v.IsActive);
        return await query.OrderBy(v => v.DisplayOrder).ThenBy(v => v.Value).ToListAsync(ct);
    }

    public async Task<LookupValue> AddLookupValueAsync(LookupValue value, CancellationToken ct = default)
    {
        var lookupType = await _context.LookupTypes.FirstOrDefaultAsync(lt => lt.Code == value.TypeCode, ct);
        if (lookupType == null)
        {
            lookupType = new LookupType
            {
                Id = Guid.NewGuid(),
                Code = value.TypeCode,
                Name = value.TypeCode,
                Description = $"Lookup type for {value.TypeCode}",
                CreatedAt = DateTime.UtcNow
            };
            _context.LookupTypes.Add(lookupType);
            await _context.SaveChangesAsync(ct);
        }

        value.Id = Guid.NewGuid();
        value.LookupTypeId = lookupType.Id;
        value.CreatedAt = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(value.Label)) value.Label = value.Value;

        _context.LookupValues.Add(value);
        await _context.SaveChangesAsync(ct);
        return value;
    }

    public async Task<LookupValue?> UpdateLookupValueAsync(Guid id, LookupValue updated, CancellationToken ct = default)
    {
        var target = await _context.LookupValues.FindAsync(new object[] { id }, ct);
        if (target == null) return null;

        target.Value = updated.Value;
        target.Label = updated.Label;
        target.DisplayOrder = updated.DisplayOrder;
        target.IsActive = updated.IsActive;
        target.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return target;
    }

    public async Task<bool> DeleteLookupValueAsync(Guid id, CancellationToken ct = default)
    {
        var target = await _context.LookupValues.FindAsync(new object[] { id }, ct);
        if (target == null) return false;

        _context.LookupValues.Remove(target);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<LookupValue?> GetLookupValueAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.LookupValues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
    }

    public async Task<bool> LookupValueExistsAsync(string typeCode, string value, Guid? excludeId = null, CancellationToken ct = default)
    {
        return await _context.LookupValues.AnyAsync(v =>
            v.TypeCode == typeCode &&
            v.Value.ToLower() == value.ToLower() &&
            (excludeId == null || v.Id != excludeId), ct);
    }

    // Case Management Settings Implementation
    public async Task<IEnumerable<CaseTypeConfig>> GetCaseTypesAsync(bool activeOnly = true, CancellationToken ct = default)
    {
        var query = _context.CaseTypeConfigs.AsNoTracking();
        if (activeOnly) query = query.Where(c => c.IsActive);
        return await query.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<CaseTypeConfig> AddCaseTypeAsync(CaseTypeConfig config, CancellationToken ct = default)
    {
        config.Id = Guid.NewGuid();
        config.CreatedAt = DateTime.UtcNow;
        _context.CaseTypeConfigs.Add(config);
        await _context.SaveChangesAsync(ct);
        return config;
    }

    public async Task<CaseTypeConfig?> UpdateCaseTypeAsync(Guid id, CaseTypeConfig updated, CancellationToken ct = default)
    {
        var target = await _context.CaseTypeConfigs.FindAsync(new object[] { id }, ct);
        if (target == null) return null;

        target.Code = updated.Code;
        target.Name = updated.Name;
        target.Prefix = updated.Prefix;
        target.DisplayOrder = updated.DisplayOrder;
        target.IsActive = updated.IsActive;
        target.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return target;
    }

    public async Task<bool> CaseTypeExistsAsync(string code, string name, Guid? excludeId = null, CancellationToken ct = default)
    {
        return await _context.CaseTypeConfigs.AnyAsync(c =>
            (c.Code.ToLower() == code.ToLower() || c.Name.ToLower() == name.ToLower()) &&
            (excludeId == null || c.Id != excludeId), ct);
    }

    public async Task<bool> DeleteCaseTypeAsync(Guid id, CancellationToken ct = default)
    {
        var target = await _context.CaseTypeConfigs.FindAsync(new object[] { id }, ct);
        if (target == null) return false;

        _context.CaseTypeConfigs.Remove(target);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IEnumerable<DepartmentSubCategory>> GetSubCategoriesAsync(Guid? departmentId = null, bool activeOnly = true, CancellationToken ct = default)
    {
        var query = _context.DepartmentSubCategories.Include(s => s.Department).AsNoTracking();
        if (departmentId.HasValue && departmentId.Value != Guid.Empty) query = query.Where(s => s.DepartmentId == departmentId.Value);
        if (activeOnly) query = query.Where(s => s.IsActive);
        return await query.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<DepartmentSubCategory> AddSubCategoryAsync(DepartmentSubCategory subCategory, CancellationToken ct = default)
    {
        subCategory.Id = Guid.NewGuid();
        subCategory.CreatedAt = DateTime.UtcNow;
        _context.DepartmentSubCategories.Add(subCategory);
        await _context.SaveChangesAsync(ct);
        await _context.Entry(subCategory).Reference(s => s.Department).LoadAsync(ct);
        return subCategory;
    }

    public async Task<DepartmentSubCategory?> UpdateSubCategoryAsync(Guid id, DepartmentSubCategory updated, CancellationToken ct = default)
    {
        var target = await _context.DepartmentSubCategories.FindAsync(new object[] { id }, ct);
        if (target == null) return null;

        target.Name = updated.Name;
        target.Code = updated.Code;
        target.Description = updated.Description;
        target.DisplayOrder = updated.DisplayOrder;
        target.IsActive = updated.IsActive;
        target.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        await _context.Entry(target).Reference(t => t.Department).LoadAsync(ct);
        return target;
    }

    public async Task<bool> SubCategoryExistsAsync(Guid departmentId, string name, Guid? excludeId = null, CancellationToken ct = default)
    {
        return await _context.DepartmentSubCategories.AnyAsync(s =>
            s.DepartmentId == departmentId &&
            s.Name.ToLower() == name.ToLower() &&
            (excludeId == null || s.Id != excludeId), ct);
    }

    public async Task<bool> DepartmentExistsAsync(Guid departmentId, CancellationToken ct = default)
    {
        return await _context.Departments.AnyAsync(d => d.Id == departmentId, ct);
    }

    public async Task<bool> DeleteSubCategoryAsync(Guid id, CancellationToken ct = default)
    {
        var target = await _context.DepartmentSubCategories.FindAsync(new object[] { id }, ct);
        if (target == null) return false;

        _context.DepartmentSubCategories.Remove(target);

        // The sub-category's priority mapping is removed with it (database cascade).
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> CountCasesBySeverityAsync(string severity, CancellationToken ct = default)
    {
        return await _context.Cases.CountAsync(c => c.Severity.ToLower() == severity.ToLower(), ct);
    }

    public async Task<int> CountCasesByCaseTypeAsync(string code, string name, CancellationToken ct = default)
    {
        var lowerCode = code.ToLower();
        var lowerName = name.ToLower();
        return await _context.Cases.CountAsync(c => c.CaseType.ToLower() == lowerCode || c.CaseType.ToLower() == lowerName, ct);
    }

    public async Task<int> CountCasesBySubCategoryAsync(Guid departmentId, string name, CancellationToken ct = default)
    {
        var lowerName = name.ToLower();
        return await _context.Cases.CountAsync(
            c => c.DepartmentId == departmentId && c.Subcategory != null && c.Subcategory.ToLower() == lowerName, ct);
    }

    /// <summary>
    /// Renaming a severity has to carry the cases already stored under the old name across,
    /// otherwise those rows would point at a severity that no longer exists.
    /// </summary>
    public async Task<int> RenameCaseSeverityAsync(string oldSeverity, string newSeverity, CancellationToken ct = default)
    {
        return await _context.Cases
            .Where(c => c.Severity.ToLower() == oldSeverity.ToLower())
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.Severity, newSeverity), ct);
    }
}
