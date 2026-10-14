namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
[RequirePermission(null, Permissions.ConfigManage)]
public class ConfigurableSettingsController : BaseApiController
{
    private readonly IConfigurableSettingsService _settingsService;
    public ConfigurableSettingsController(IConfigurableSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    [HttpGet("fields")]
    public async Task<IActionResult> GetFields([FromQuery] string moduleKey = "Customer360", [FromQuery] string? sectionKey = null, CancellationToken ct = default)
    {
        var fields = await _settingsService.GetFieldConfigurationsAsync(moduleKey, sectionKey, ct);
        return Ok(fields);
    }

    /// <summary>
    /// Save Changes: existing fields in <c>update</c> (matched by id), new ones in <c>create</c> (no id; the server
    /// issues it). Atomic, and answers with the section as stored so the page shows exactly what was saved.
    /// </summary>
    [HttpPut("fields")]
    public async Task<IActionResult> SaveFields([FromBody] UpdateFieldConfigurationsRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ModuleKey) || string.IsNullOrWhiteSpace(request.SectionKey))
        {
            return BadRequest(new { message = "ModuleKey and SectionKey are required." });
        }

        var saved = await _settingsService.SaveFieldConfigurationsAsync(request, ct);
        return Ok(new { message = "Field configurations saved successfully.", fields = saved });
    }

    [HttpPost("fields")]
    public async Task<IActionResult> AddCustomField([FromBody] CreateCustomFieldDto dto, CancellationToken ct = default)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.DisplayLabel))
        {
            return BadRequest(new { message = "DisplayLabel is required." });
        }

        var created = await _settingsService.AddCustomFieldAsync(dto, ct);
        return Ok(created);
    }

    [HttpPut("fields/{id:guid}")]
    public async Task<IActionResult> UpdateField(Guid id, [FromBody] UpdateFieldConfigurationDto dto, CancellationToken ct = default)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.DisplayLabel))
        {
            return BadRequest(new { message = "DisplayLabel is required." });
        }

        var updated = await _settingsService.UpdateFieldConfigurationAsync(id, dto, ct);
        if (updated == null) return NotFound(new { message = "Field configuration not found." });
        return Ok(updated);
    }

    /// <summary>Dry run of a type change (and rule changes) for the editor: would existing data still fit? The save runs the same check.</summary>
    [HttpPost("fields/{id:guid}/check-type")]
    public async Task<IActionResult> CheckFieldType(Guid id, [FromBody] FieldConfigurationDto proposal, CancellationToken ct = default)
    {
        var result = await _settingsService.CheckTypeChangeAsync(id, proposal, ct);
        return result == null ? NotFound(new { message = "Field configuration not found." }) : Ok(result);
    }

    /// <summary>The format rules an ID type option can use.</summary>
    [HttpGet("id-format-rules")]
    public IActionResult GetIdFormatRules() => Ok(IdFormatRules.All);

    [HttpDelete("fields/{id:guid}")]
    public async Task<IActionResult> DeleteCustomField(Guid id, CancellationToken ct = default)
    {
        var success = await _settingsService.DeleteFieldConfigurationAsync(id, ct);
        if (!success) return NotFound(new { message = "Field configuration not found." });
        return Ok(new { message = "Field deleted successfully." });
    }

    /// <summary>The configured lists (lookup types), e.g. for choosing the options of a custom dropdown field.</summary>
    [HttpGet("lookups")]
    public async Task<IActionResult> GetLookupTypes(CancellationToken ct = default)
        => Ok(await _settingsService.GetLookupTypesAsync(ct));

    [HttpGet("lookups/{typeCode}")]
    public async Task<IActionResult> GetLookupValues(string typeCode, [FromQuery] bool activeOnly = true, CancellationToken ct = default)
    {
        var values = await _settingsService.GetLookupValuesAsync(typeCode, activeOnly, ct);
        return Ok(values);
    }

    /// <summary>Saves all pending option changes of one list in a single transaction (used by the field drawer).</summary>
    [HttpPut("lookups/{typeCode}")]
    public async Task<IActionResult> SaveLookupValues(string typeCode, [FromBody] SaveLookupValuesRequest request, CancellationToken ct = default)
    {
        if (request == null) return BadRequest(new { message = "Values are required." });
        return Ok(await _settingsService.SaveLookupValuesAsync(typeCode, request, ct));
    }

    [HttpPost("lookups")]
    public async Task<IActionResult> AddLookupValue([FromBody] CreateLookupValueDto dto, CancellationToken ct = default)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.TypeCode) || string.IsNullOrWhiteSpace(dto.Value))
        {
            return BadRequest(new { message = "TypeCode and Value are required." });
        }

        var created = await _settingsService.AddLookupValueAsync(dto, ct);
        return Ok(created);
    }

    [HttpPut("lookups/{id:guid}")]
    public async Task<IActionResult> UpdateLookupValue(Guid id, [FromBody] UpdateLookupValueDto dto, CancellationToken ct = default)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Value))
        {
            return BadRequest(new { message = "Value is required." });
        }

        var updated = await _settingsService.UpdateLookupValueAsync(id, dto, ct);
        if (updated == null) return NotFound(new { message = "Lookup value not found." });
        return Ok(updated);
    }

    [HttpDelete("lookups/{id:guid}")]
    public async Task<IActionResult> DeleteLookupValue(Guid id, CancellationToken ct = default)
    {
        var success = await _settingsService.DeleteLookupValueAsync(id, ct);
        if (!success) return NotFound(new { message = "Lookup value not found." });
        return Ok(new { message = "Lookup value deleted successfully." });
    }

    // Case Management Settings Endpoints
    [HttpGet("casetypes")]
    public async Task<IActionResult> GetCaseTypes([FromQuery] bool activeOnly = true, CancellationToken ct = default)
    {
        var types = await _settingsService.GetCaseTypesAsync(activeOnly, ct);
        return Ok(types);
    }

    [HttpPost("casetypes")]
    public async Task<IActionResult> AddCaseType([FromBody] CreateCaseTypeConfigDto dto, CancellationToken ct = default)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Code))
        {
            return BadRequest(new { message = "Code is required." });
        }

        var created = await _settingsService.AddCaseTypeAsync(dto, ct);
        return Ok(created);
    }

    [HttpPut("casetypes/{id:guid}")]
    public async Task<IActionResult> UpdateCaseType(Guid id, [FromBody] UpdateCaseTypeConfigDto dto, CancellationToken ct = default)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Code))
        {
            return BadRequest(new { message = "Code is required." });
        }

        var updated = await _settingsService.UpdateCaseTypeAsync(id, dto, ct);
        if (updated == null) return NotFound(new { message = "Case type not found." });
        return Ok(updated);
    }

    [HttpDelete("casetypes/{id:guid}")]
    public async Task<IActionResult> DeleteCaseType(Guid id, CancellationToken ct = default)
    {
        var success = await _settingsService.DeleteCaseTypeAsync(id, ct);
        if (!success) return NotFound(new { message = "Case type not found." });
        return Ok(new { message = "Case type deleted successfully." });
    }

    [HttpGet("subcategories")]
    public async Task<IActionResult> GetSubCategories([FromQuery] Guid? departmentId = null, [FromQuery] bool activeOnly = true, CancellationToken ct = default)
    {
        var items = await _settingsService.GetSubCategoriesAsync(departmentId, activeOnly, ct);
        return Ok(items);
    }

    [HttpPost("subcategories")]
    public async Task<IActionResult> AddSubCategory([FromBody] CreateDepartmentSubCategoryDto dto, CancellationToken ct = default)
    {
        if (dto == null || dto.DepartmentId == Guid.Empty || string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new { message = "DepartmentId and Name are required." });
        }

        var created = await _settingsService.AddSubCategoryAsync(dto, ct);
        return Ok(created);
    }

    [HttpPut("subcategories/{id:guid}")]
    public async Task<IActionResult> UpdateSubCategory(Guid id, [FromBody] UpdateDepartmentSubCategoryDto dto, CancellationToken ct = default)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new { message = "Name is required." });
        }

        var updated = await _settingsService.UpdateSubCategoryAsync(id, dto, ct);
        if (updated == null) return NotFound(new { message = "Sub-category not found." });
        return Ok(updated);
    }

    [HttpDelete("subcategories/{id:guid}")]
    public async Task<IActionResult> DeleteSubCategory(Guid id, CancellationToken ct = default)
    {
        var success = await _settingsService.DeleteSubCategoryAsync(id, ct);
        if (!success) return NotFound(new { message = "Sub-category not found." });
        return Ok(new { message = "Sub-category deleted successfully." });
    }

    // Severity master data (drives SLA Configuration and the Create Case severity dropdown)
    [HttpGet("severities")]
    public async Task<IActionResult> GetSeverities(CancellationToken ct = default)
    {
        var severities = await _settingsService.GetSeverityConfigurationsAsync(ct);
        return Ok(severities);
    }

    [HttpPost("severities")]
    public async Task<IActionResult> AddSeverity([FromBody] CreateSeverityDto dto, CancellationToken ct = default)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new { message = "Severity name is required." });
        }

        var created = await _settingsService.AddSeverityAsync(dto, ct);
        return Ok(created);
    }

    [HttpPut("severities/{id:guid}")]
    public async Task<IActionResult> UpdateSeverity(Guid id, [FromBody] UpdateSeverityDto dto, CancellationToken ct = default)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new { message = "Severity name is required." });
        }

        var updated = await _settingsService.UpdateSeverityAsync(id, dto, ct);
        if (updated == null) return NotFound(new { message = "Severity not found." });
        return Ok(updated);
    }

    [HttpDelete("severities/{id:guid}")]
    public async Task<IActionResult> DeleteSeverity(Guid id, CancellationToken ct = default)
    {
        var success = await _settingsService.DeleteSeverityAsync(id, ct);
        if (!success) return NotFound(new { message = "Severity not found." });
        return Ok(new { message = "Severity deleted successfully." });
    }
}
