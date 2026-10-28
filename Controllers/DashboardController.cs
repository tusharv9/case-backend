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

    /// <summary>
    /// The Reports page's data as a CSV download: every case its filters select (the whole set, not one page), within the
    /// cases the caller may see and masked as on screen. X-Export-Row-Count / X-Export-Truncated say what the file holds.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] Guid? departmentId,
        [FromQuery] string? caseType,
        [FromQuery] string? status,
        [FromQuery] string? severity,
        [FromQuery] string? dateRange,
        [FromQuery] string? customStartDate,
        [FromQuery] string? customEndDate,
        [FromQuery] bool? myCasesOnly,
        [FromQuery] string? slaStatus,
        [FromQuery] string? search,
        CancellationToken ct)
    {
        var export = await _dashboard.ExportAsync(new DashboardQuery(
            departmentId, caseType, status, severity, dateRange, customStartDate, customEndDate,
            myCasesOnly == true ? CurrentUserId : null), slaStatus, search, ct);

        Response.Headers["X-Export-Row-Count"] = export.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Export-Truncated"] = export.Truncated ? "true" : "false";
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(ReportCsv.ToBytes(export.Csv), "text/csv; charset=utf-8", $"case-management-report-{export.GeneratedOn:yyyy-MM-dd}.csv");
    }

    /// <summary>Every option the dashboard's filter bar offers, in one request.</summary>
    [HttpGet("filters")]
    public async Task<IActionResult> GetFilters(CancellationToken ct) => Ok(await _dashboard.GetFiltersAsync(ct));
}
