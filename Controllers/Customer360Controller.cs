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

    // ---------------------------------------------------------------------------------------- Open Customer 360

    /// <summary>The Host permissions that let a person open a customer profile in Customer 360.</summary>
    private static readonly string[] Customer360ProfileView = ["remote.customer360.profile:View", "remote.customer360:View"];

    public sealed class RedeemRequest
    {
        public string? Code { get; set; }
    }

    /// <summary>
    /// A one-time code that opens this case's customer in Customer 360 (see <see cref="Customer360Handoff"/>). The
    /// identifier itself never leaves the server here; only the person who asked can redeem the code, once.
    /// </summary>
    [HttpPost("/api/cases/{caseId:guid}/customer360/handoff")]
    [CaseAccess("caseId")]
    [RequirePermission(Permissions.CasesRead)]
    public async Task<IActionResult> HandoffForCase(Guid caseId, [FromServices] ICustomer360Handoff handoff,
        [FromServices] ICurrentUserAccessor user, CancellationToken ct)
    {
        if (!MayOpenCustomer360(user)) return Forbid();
        var caseItem = await context.Cases.AsNoTracking().FirstOrDefaultAsync(c => c.Id == caseId, ct);
        if (caseItem == null) return NotFound(new { error = "Case not found." });
        return Ok(new { code = handoff.Issue(customers.HandoffForCase(caseItem), Person(user)) });
    }

    /// <summary>The same, for the customer just chosen on Create Case (before any case exists).</summary>
    [HttpPost("handoff")]
    public async Task<IActionResult> HandoffForSelection([FromBody] Customer360SelectionDto selection,
        [FromServices] ICustomer360Handoff handoff, [FromServices] ICurrentUserAccessor user, CancellationToken ct)
    {
        if (!MayOpenCustomer360(user)) return Forbid();
        return Ok(new { code = handoff.Issue(await customers.HandoffForSelectionAsync(selection, ct), Person(user)) });
    }

    /// <summary>
    /// Customer 360 exchanging a code for the customer to open. Called from Customer 360 by the same signed-in person,
    /// who may hold no Case Management permission at all — what is required is that they may open Customer 360
    /// profiles, because the answer carries the unmasked identifier Customer 360 searches by.
    /// </summary>
    [HttpPost("handoff/redeem")]
    [NoPermissionRequired]
    public IActionResult Redeem([FromBody] RedeemRequest request, [FromServices] ICustomer360Handoff handoff, [FromServices] ICurrentUserAccessor user)
    {
        if (user.Principal == null) return Unauthorized();
        if (!MayOpenCustomer360(user)) return Forbid();
        var target = handoff.Redeem(request.Code ?? string.Empty, Person(user));
        return target == null
            ? NotFound(new { error = "This link to Customer 360 has expired or was already used. Open it again from the case." })
            : Ok(target);
    }

    private static string Person(ICurrentUserAccessor user) => user.Principal?.ExternalUserId ?? user.LocalUserId?.ToString() ?? string.Empty;

    private static bool MayOpenCustomer360(ICurrentUserAccessor user) =>
        user.Principal is { } p && (p.IsAdministrator || p.Permissions.Any(perm => Customer360ProfileView.Contains(perm, StringComparer.Ordinal)));
}
