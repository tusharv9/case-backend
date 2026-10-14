namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
[RequirePermission(Permissions.AuditView)]
public class AuditController : BaseApiController
{
    private readonly ICaseService _caseService;

    public AuditController(ICaseService caseService)
    {
        _caseService = caseService;
    }

    [HttpGet]
    [HttpGet("cases")]
    public async Task<IActionResult> GetAuditLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] string? actionType = null, [FromQuery] string? search = null, CancellationToken ct = default)
    {
        var logs = await _caseService.GetCaseAuditEventsAsync(page, pageSize, actionType, search, ct);
        return Ok(logs);
    }
}
