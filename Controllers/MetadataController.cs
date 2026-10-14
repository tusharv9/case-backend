namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// One call per form with everything it needs to render and validate itself. Open to every signed-in user: it only
/// carries the configuration they must already see to use the form.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class MetadataController : BaseApiController
{
    private readonly IMetadataService _metadata;
    private readonly ICountryService _countries;

    public MetadataController(IMetadataService metadata, ICountryService countries)
    {
        _metadata = metadata;
        _countries = countries;
    }

    [HttpGet("case-form")]
    public async Task<IActionResult> GetCaseForm(CancellationToken ct) => Ok(await _metadata.GetCaseFormAsync(ct));

    [HttpGet("customer-form")]
    public async Task<IActionResult> GetCustomerForm(CancellationToken ct) => Ok(await _metadata.GetCustomerFormAsync(ct));

    /// <summary>Countries for phone numbers (name, ISO codes, dial code and the digit rules their numbers follow).</summary>
    [HttpGet("countries")]
    public async Task<IActionResult> GetCountries(CancellationToken ct) => Ok(await _countries.GetActiveAsync(ct));

    /// <summary>What each field type lets an administrator configure — the field drawer shows exactly these settings.</summary>
    [HttpGet("field-types")]
    public IActionResult GetFieldTypes() => Ok(FieldDefinitionRules.ByType.Select(kv => new
    {
        type = kv.Key, length = kv.Value.Length, pattern = kv.Value.Pattern, range = kv.Value.Range, lookup = kv.Value.Lookup, masking = kv.Value.Masking
    }));
}
