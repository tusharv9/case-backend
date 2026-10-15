namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services.Customer360;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Existing customers from Customer 360 — the browser's only route to them from Case Management.
/// </summary>
/// <remarks>
/// <para>Customer 360 stays the source of truth: these endpoints read it with the caller's own token (so it
/// enforces its own access and audits each lookup under that person) and return Case Management's shape,
/// masked by the Customer 360 field settings. Nothing read here is stored, except — when a case is created —
/// the identifiers <see cref="Services.CaseService"/> keeps.</para>
///
/// <para>Searches are POSTs: the identifier travels in the body, never in a URL that proxies and access logs keep.</para>
///
/// <para>400 for a value to correct, 403 when Customer 360 refuses the person, 503 when it cannot be reached.</para>
/// </remarks>
[ApiController]
[Route("api/customer360")]
[RequirePermission(Permissions.CustomersRead)]
public class Customer360Controller(ICustomer360CustomerService customers, AppDbContext context) : BaseApiController
{
    public sealed class SearchRequest
    {
        public string? CustomerType { get; set; }
        public string? SearchBy { get; set; }
        public string? Value { get; set; }
    }

    /// <summary>How existing customers can be looked up: Customer 360's own identifier searches.</summary>
    [HttpGet("search-options")]
    public async Task<IActionResult> GetSearchOptions(CancellationToken ct) => Ok(await customers.GetSearchOptionsAsync(ct));

    /// <summary>Every Customer 360 customer matching one identifier, for the person to choose from.</summary>
    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] SearchRequest request, CancellationToken ct) =>
        Ok(await customers.SearchAsync(request.CustomerType, request.SearchBy, request.Value, ct));

    /// <summary>
    /// A case's Customer 360 customer, read live. Always 200 with a status, so the case still opens when Customer 360
    /// cannot be read (the card says why, and never shows stale or invented details).
    /// </summary>
    [HttpGet("/api/cases/{caseId:guid}/customer360")]
    [CaseAccess("caseId")]
    [RequirePermission(Permissions.CasesRead)]
    public async Task<IActionResult> GetCaseCustomer(Guid caseId, CancellationToken ct)
    {
        var caseItem = await context.Cases.AsNoTracking().FirstOrDefaultAsync(c => c.Id == caseId, ct);
        if (caseItem == null) return NotFound(new { error = "Case not found." });
        return Ok(await customers.GetForCaseAsync(caseItem, ct));
    }
}
