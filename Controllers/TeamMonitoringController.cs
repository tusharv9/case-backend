namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Data;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Team Monitoring. Everything here is limited to the teams the caller monitors (all of them with cases:ViewAll, otherwise the
/// teams they lead or belong to); an agent outside that scope answers 404, exactly like one who does not exist.
/// </summary>
[ApiController]
[Route("api/team-monitoring")]
[RequirePermission(Permissions.MonitoringView)]
public class TeamMonitoringController : BaseApiController
{
    private readonly ITeamMonitoringService _monitoringService;
    private readonly IAuditLogService _audit;
    private readonly HostSignInHistoryClient _signIns;
    private readonly AppDbContext _db;

    public TeamMonitoringController(ITeamMonitoringService monitoringService, IAuditLogService audit, HostSignInHistoryClient signIns, AppDbContext db)
    {
        _monitoringService = monitoringService;
        _audit = audit;
        _signIns = signIns;
        _db = db;
    }

    /// <summary>The whole monitor in one request: every team in scope ("All teams"), or one team (<c>?teamId=</c>).</summary>
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview([FromQuery] Guid? teamId, [FromQuery] int periodDays = 7, CancellationToken ct = default)
    {
        try
        {
            return Ok(await _monitoringService.GetOverviewAsync(new MonitoringQuery(teamId, periodDays), ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("agents/{agentId:guid}")]
    public async Task<IActionResult> GetAgent(Guid agentId, [FromQuery] int periodDays = 30, CancellationToken ct = default) =>
        Ok(await _monitoringService.GetAgentAsync(agentId, periodDays, ct));

    [HttpGet("agents/{agentId:guid}/cases")]
    public async Task<IActionResult> GetAgentCases(Guid agentId, [FromQuery] string scope = "open", [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default) =>
        Ok(await _monitoringService.GetAgentCasesAsync(agentId, scope, page, pageSize, ct));

    /// <summary>
    /// What the agent did, from the audit trail — the same rows the Case Audit Trail would show for them, held to the caller's
    /// case visibility (someone who does not see every team sees only the agent's actions on cases they can see).
    /// </summary>
    [HttpGet("agents/{agentId:guid}/activity")]
    public async Task<IActionResult> GetAgentActivity(Guid agentId, [FromQuery] string? actionType, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        await _monitoringService.EnsureCanMonitorAsync(agentId, ct);
        if (string.Equals(actionType, "signin", StringComparison.OrdinalIgnoreCase))
        {
            // Sign-ins are the Host's records, read with the viewer's own Host access (see HostSignInHistoryClient).
            var hostId = await _db.Users.AsNoTracking().Where(u => u.Id == agentId).Select(u => u.ExternalUserId).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(hostId))
                return NotFound(new { error = "This person exists only in Case Management, so the Host has no sign-in history for them." });
            try
            {
                return Ok(await _signIns.ListAsync(hostId, agentId, from, to, page, pageSize, ct));
            }
            catch (HostSignInHistoryClient.UnavailableException ex)
            {
                return StatusCode(ex.Status, new { error = ex.Message, code = ex.Status == 403 ? "SignInHistoryForbidden" : "SignInHistoryUnavailable" });
            }
        }
        var filter = new AuditQueryParameters { ActionType = actionType, ActorId = agentId, From = from, To = to }.ToFilter();
        return Ok(await _audit.ListAsync(filter, page, Math.Clamp(pageSize, 1, 50), ct));
    }

    /// <summary>
    /// How many rows Export CSV would write for these choices (the drawer shows it before anything is downloaded).
    /// <c>kind</c> = <c>summary</c> (one row per agent) or <c>activity</c> (the agents' recorded actions).
    /// </summary>
    [HttpGet("export/count")]
    public async Task<IActionResult> CountExport([FromQuery] MonitoringExportParameters q, CancellationToken ct = default)
    {
        var agents = await _monitoringService.GetExportAgentsAsync(q.ToQuery(), ct);
        if (!q.IsActivity) return Ok(new { count = agents.Count, agents = agents.Count });
        var count = agents.Count == 0 ? 0 : await _audit.CountAsync(q.ActivityFilter(agents.Select(a => a.UserId).ToList()), ct);
        return Ok(new { count, agents = agents.Count });
    }

    /// <summary>
    /// The CSV, built on the server from the whole matching set within the caller's monitoring scope (never only the rows on
    /// screen), values made formula-safe. Activity is capped like the audit trail's export (newest first).
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] MonitoringExportParameters q, [FromQuery] int limit = 1000, CancellationToken ct = default)
    {
        ReportExport export;
        string name;
        if (q.IsActivity)
        {
            var agents = await _monitoringService.GetExportAgentsAsync(q.ToQuery(), ct);
            export = agents.Count == 0
                ? new ReportExport(ReportCsv.Build(new[] { "Timestamp" }, Array.Empty<IReadOnlyList<string?>>()), 0, false, DateTime.UtcNow.Date)
                : await _audit.ExportAsync(q.ActivityFilter(agents.Select(a => a.UserId).ToList()), limit, ct);
            name = "team-monitoring-activity";
        }
        else
        {
            export = await _monitoringService.ExportAgentSummaryAsync(q.ToQuery(), ct);
            name = "team-monitoring-agents";
        }

        Response.Headers["X-Export-Row-Count"] = export.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Export-Truncated"] = export.Truncated ? "true" : "false";
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(ReportCsv.ToBytes(export.Csv), "text/csv; charset=utf-8", $"{name}-{export.GeneratedOn:yyyy-MM-dd}.csv");
    }

    [HttpPost("nudge/{agentId:guid}")]
    [RequirePermission(Permissions.MonitoringNudge)]
    public async Task<IActionResult> NudgeAgent(Guid agentId, [FromBody] NudgeAgentDto? dto, CancellationToken ct) =>
        Ok(await _monitoringService.NudgeAgentAsync(agentId, dto?.Reason, CurrentUserId, ct));
}

/// <summary>Team Monitoring → Export CSV query string (shared by the count and the export).</summary>
public sealed class MonitoringExportParameters
{
    /// <summary><c>summary</c> (default) or <c>activity</c>.</summary>
    public string? Kind { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? AgentId { get; set; }
    /// <summary>Available, On interaction, Break, Offline, or <c>notset</c>; empty for every state.</summary>
    public string? State { get; set; }
    public int PeriodDays { get; set; } = 7;
    public string? ActionType { get; set; }
    /// <summary>ISO instants; <c>to</c> is exclusive.</summary>
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    public bool IsActivity => string.Equals(Kind, "activity", StringComparison.OrdinalIgnoreCase);

    public MonitoringExportQuery ToQuery() => new(TeamId, AgentId, State, PeriodDays, ActionType, From, To);

    public AuditFilter ActivityFilter(IReadOnlyCollection<Guid> agentIds)
    {
        var range = new AuditQueryParameters { ActionType = ActionType, From = From, To = To }.ToFilter();   // validates the range
        return range with { ActorIds = agentIds };
    }
}
