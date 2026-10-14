namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using Microsoft.EntityFrameworkCore;

public interface IMetadataService
{
    Task<CaseFormMetadataDto> GetCaseFormAsync(CancellationToken ct = default);
    Task<CustomerFormMetadataDto> GetCustomerFormAsync(CancellationToken ct = default);
}

/// <summary>
/// Assembles what a form needs to render and validate itself — its field configuration plus the options of every
/// list those fields use — in one cached response, instead of the browser making 5–8 separate calls (one of which
/// used to be the entire SLA configuration, with every user and holiday). The cache is cleared whenever any of this
/// configuration changes (see ConfigChangeInterceptor).
/// </summary>
public class MetadataService : IMetadataService
{
    private readonly AppDbContext _context;
    private readonly IConfigCache _cache;

    public MetadataService(AppDbContext context, IConfigCache cache)
    {
        _context = context;
        _cache = cache;
    }

    public Task<CaseFormMetadataDto> GetCaseFormAsync(CancellationToken ct = default) =>
        _cache.GetOrCreateAsync("metadata:case-form", async () =>
        {
            var fields = await LoadFieldsAsync("CaseManagement", "CreateCase");

            var caseTypes = await _context.CaseTypeConfigs.AsNoTracking()
                .Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
                .Select(c => new CaseTypeOptionDto { Code = c.Code, Name = c.Name, Prefix = c.Prefix })
                .ToListAsync(ct);

            var departments = await _context.Departments.AsNoTracking()
                .Where(d => d.IsActive).OrderBy(d => d.Name)
                .Select(d => new DepartmentOptionDto { Id = d.Id, Name = d.Name, Code = d.Code })
                .ToListAsync(ct);
            var subCategories = await _context.DepartmentSubCategories.AsNoTracking()
                .Where(s => s.IsActive).OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name)
                .Select(s => new { s.DepartmentId, Option = new SubCategoryOptionDto { Id = s.Id, Name = s.Name, Code = s.Code, Description = s.Description } })
                .ToListAsync(ct);
            foreach (var department in departments)
                department.SubCategories = subCategories.Where(s => s.DepartmentId == department.Id).Select(s => s.Option).ToList();

            var priorities = await _context.PrioritySlaRules.AsNoTracking()
                .Where(r => r.IsActive).OrderBy(r => r.DisplayOrder)
                .Select(r => new PriorityOptionDto
                {
                    Name = r.Priority,
                    DisplayOrder = r.DisplayOrder,
                    InternalHours = (r.InternalResolutionMinutes + 59) / 60,
                    ExternalHours = (r.ExternalResolutionMinutes + 59) / 60,
                    FirstResponseMinutes = r.FirstResponseMinutes
                })
                .ToListAsync(ct);

            return new CaseFormMetadataDto
            {
                Fields = fields,
                CaseTypes = caseTypes,
                Departments = departments,
                Priorities = priorities,
                Lookups = await LoadLookupsAsync(fields, ct)
            };
        });

    public Task<CustomerFormMetadataDto> GetCustomerFormAsync(CancellationToken ct = default) =>
        _cache.GetOrCreateAsync("metadata:customer-form", async () =>
        {
            var fields = await LoadFieldsAsync("Customer360", "AddNewCustomer");
            return new CustomerFormMetadataDto { Fields = fields, Lookups = await LoadLookupsAsync(fields, ct) };
        });

    private async Task<List<FieldConfigurationDto>> LoadFieldsAsync(string module, string section)
    {
        var rows = await _context.FieldConfigurations.AsNoTracking()
            .Where(f => f.ModuleKey == module && f.SectionKey == section)
            .OrderBy(f => f.DisplayOrder)
            .ToListAsync();
        return rows.Select(FieldConfigurationDto.From).ToList();
    }

    private async Task<Dictionary<string, List<LookupOptionDto>>> LoadLookupsAsync(IEnumerable<FieldConfigurationDto> fields, CancellationToken ct)
    {
        var codes = fields.Where(f => !string.IsNullOrWhiteSpace(f.LookupTypeCode)).Select(f => f.LookupTypeCode!).Distinct().ToList();
        var values = await _context.LookupValues.AsNoTracking()
            .Where(v => v.IsActive && codes.Contains(v.TypeCode))
            .OrderBy(v => v.DisplayOrder).ThenBy(v => v.Label)
            .Select(v => new { v.TypeCode, v.LookupType!.UsesFormatRules, v.FormatRule, v.FormatRegex, v.FormatMessage, v.Value, v.Label })
            .ToListAsync(ct);

        return codes.ToDictionary(c => c, c => values.Where(v => v.TypeCode == c).Select(v => new LookupOptionDto
        {
            Value = v.Value,
            Label = v.Label,
            // The effective rule (the built-in default when none was chosen), so the form checks exactly what the server will.
            FormatRule = v.UsesFormatRules ? v.FormatRule ?? IdFormatRules.DefaultFor(v.Value) : null,
            FormatRegex = v.UsesFormatRules ? v.FormatRegex : null,
            FormatMessage = v.UsesFormatRules ? v.FormatMessage : null,
        }).ToList());
    }
}
