namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>The Case Audit Trail: list, filter choices, matching count and CSV export — all limited to what the caller may see.</summary>
[ApiController]
[Route("api/[controller]")]
[RequirePermission(Permissions.AuditView)]
public class AuditController : BaseApiController
{
    private readonly IAuditLogService _audit;

    public AuditController(IAuditLogService audit)
    {
        _audit = audit;
    }

    [HttpGet]
    [HttpGet("cases")]
    public async Task<IActionResult> GetAuditLogs([FromQuery] AuditQueryParameters q, [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default) =>
        Ok(await _audit.ListAsync(q.ToFilter(), page, pageSize, ct));

    [HttpGet("filters")]
    public async Task<IActionResult> GetFilters(CancellationToken ct) => Ok(await _audit.GetFiltersAsync(ct));

    /// <summary>How many events match, so the export dialog can say how many rows the file will hold before it is made.</summary>
    [HttpGet("count")]
    public async Task<IActionResult> Count([FromQuery] AuditQueryParameters q, CancellationToken ct) =>
        Ok(new { count = await _audit.CountAsync(q.ToFilter(), ct), maxExportRows = AuditLogService.MaxExportRows });

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] AuditQueryParameters q, [FromQuery] int limit = 1000, CancellationToken ct = default)
    {
        var export = await _audit.ExportAsync(q.ToFilter(), limit, ct);

        Response.Headers["X-Export-Row-Count"] = export.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Export-Truncated"] = export.Truncated ? "true" : "false";
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(ReportCsv.ToBytes(export.Csv), "text/csv; charset=utf-8", $"case-audit-trail-{export.GeneratedOn:yyyy-MM-dd}.csv");
    }
}

/// <summary>The audit trail's query-string filters (shared by the list, the count and the export).</summary>
public sealed class AuditQueryParameters
{
    public string? ActionType { get; set; }
    public string? Search { get; set; }
    public Guid? ActorId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? DepartmentId { get; set; }
    /// <summary>ISO instants; <c>to</c> is exclusive.</summary>
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? CaseNumber { get; set; }

    public AuditFilter ToFilter()
    {
        static DateTime? Utc(DateTime? d) => d.HasValue ? (d.Value.Kind == DateTimeKind.Utc ? d : d.Value.ToUniversalTime()) : null;
        if (From.HasValue && To.HasValue && Utc(From) >= Utc(To)) throw new ArgumentException("The start of the time range must be before its end.");
        return new AuditFilter(ActionType, Search, ActorId, TeamId, DepartmentId, Utc(From), Utc(To), CaseNumber);
    }
}
