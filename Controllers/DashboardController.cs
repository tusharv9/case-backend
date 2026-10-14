namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
[RequirePermission(Permissions.CasesRead)]
public class DashboardController : BaseApiController
{
    private readonly IDashboardService _dashboard;

    public DashboardController(IDashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    /// <summary>
    /// Returns server-side aggregated dashboard KPIs.
    /// The frontend should use this instead of loading every case.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetDashboardSummary(
        [FromQuery] Guid? departmentId,
        [FromQuery] string? caseType,
        [FromQuery] string? status,
        [FromQuery] string? severity,
        [FromQuery] string? dateRange,
        [FromQuery] string? customStartDate,
        [FromQuery] string? customEndDate,
        [FromQuery] bool? myCasesOnly,
        CancellationToken ct)
    {
        var summary = await _dashboard.GetSummaryAsync(new DashboardQuery(
            departmentId, caseType, status, severity, dateRange, customStartDate, customEndDate,
            myCasesOnly == true ? CurrentUserId : null), ct);

        return Ok(summary);
    }

    /// <summary>Every option the dashboard's filter bar offers, in one request.</summary>
    [HttpGet("filters")]
    public async Task<IActionResult> GetFilters(CancellationToken ct) => Ok(await _dashboard.GetFiltersAsync(ct));
}
