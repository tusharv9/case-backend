namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Configuration;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

/// <summary>
/// Backs the global header search. Replaces the previous approach of downloading the whole
/// customer and case tables into the browser and filtering them there.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[RequirePermission(Permissions.CasesRead)]
public class SearchController : BaseApiController
{
    private readonly ICustomerService _customerService;
    private readonly ICaseService _caseService;
    private readonly SearchOptions _options;

    public SearchController(ICustomerService customerService, ICaseService caseService, IOptions<SearchOptions> options)
    {
        _customerService = customerService;
        _caseService = caseService;
        _options = options.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int? limit, CancellationToken ct)
    {
        var term = q?.Trim() ?? string.Empty;

        // Too short to be meaningful — answer without touching the database.
        if (term.Length < _options.MinQueryLength)
        {
            return Ok(new GlobalSearchResultDto());
        }

        var take = Math.Clamp(limit ?? _options.DefaultResultLimit, 1, _options.MaxResultLimit);

        // Sequential rather than concurrent: both queries share one scoped DbContext, which is
        // not safe for parallel use.
        var customers = await _customerService.SearchCustomersAsync(term, take, ct);
        var cases = await _caseService.SearchCasesAsync(term, take, ct);

        return Ok(new GlobalSearchResultDto
        {
            Customers = customers.ToList(),
            Cases = cases.ToList()
        });
    }
}
