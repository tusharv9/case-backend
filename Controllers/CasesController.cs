namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Data;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Customer360;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/[controller]")]
[RequirePermission(Permissions.CasesRead, Permissions.CasesWrite)]
[CaseAccess("id")]   // every /api/cases/{id}/... request is refused (404) for a case the caller may not see
public class CasesController : BaseApiController
{
    private readonly ICaseService _caseService;
    private readonly IAttachmentService _attachments;
    private readonly ICaseCollaborationService _collaboration;

    public CasesController(ICaseService caseService, IAttachmentService attachments, ICaseCollaborationService collaboration)
    {
        _caseService = caseService;
        _attachments = attachments;
        _collaboration = collaboration;
    }

    [HttpGet]
    public async Task<IActionResult> GetBoardCases(
        [FromQuery] Guid? departmentId,
        [FromQuery] string? caseType,
        [FromQuery] string? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? search,
        [FromQuery] string? priority,
        [FromQuery] string? channel,
        [FromServices] ICaseAccessScope access,
        CancellationToken ct)
    {
        // Always paged. (A call without a page used to return every case in the database — hundreds of MB at scale.)
        var paged = await _caseService.GetPaginatedBoardCasesAsync(
            status, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 30, 1, 100),
            CrossTeamFilter(access, departmentId), caseType, search, priority, channel, ct);
        return Ok(paged);
    }

    /// <summary>Open / SLA-breached counts for the Case Management header.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetCaseStats([FromQuery] Guid? departmentId, [FromServices] ICaseAccessScope access, CancellationToken ct)
    {
        var stats = await _caseService.GetCaseStatsAsync(CrossTeamFilter(access, departmentId), ct);
        return Ok(stats);
    }

    /// <summary>
    /// Filtering by team is for people who see every team's cases (cases:ViewAll, administrators). Everyone else already sees
    /// only their own teams' and assignments' cases, and a team filter is ignored for them — it could only narrow that set in
    /// ways the screen does not offer them.
    /// </summary>
    internal static Guid? CrossTeamFilter(ICaseAccessScope access, Guid? departmentId) => access.SeesAll ? departmentId : null;

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetCaseDetails(Guid id, [FromServices] AppDbContext db, [FromServices] ILogger<CasesController> logger, CancellationToken ct)
    {
        var caseDetails = await _caseService.GetCaseDetailsAsync(id, ct);
        if (caseDetails == null) return NotFound();
        // Only a case the caller may see reaches this line (the case-access filter answers 404 otherwise).
        await CaseViewLog.RecordAsync(db, id, CurrentUserId, logger, ct);
        return Ok(caseDetails);
    }

    [HttpPost]
    [RequirePermission(Permissions.CasesCreate)]
    public async Task<IActionResult> CreateCase([FromBody] CreateCaseDto dto)
    {
        var newCase = await _caseService.CreateCaseAsync(dto, CurrentUserId);
        // The same shape every screen reads a case in (owner, team and creator named, SLA verdict, masking applied), so the
        // list can show the new case straight away. A Customer 360 customer's name is not stored; the form that chose them has it.
        var created = await _caseService.GetCaseDetailsAsync(newCase.Id);
        return CreatedAtAction(nameof(GetCaseDetails), new { id = newCase.Id }, (object?)created ?? new { id = newCase.Id, caseNumber = newCase.CaseNumber });
    }

    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateCaseStatusDto dto, [FromServices] ICurrentUserAccessor user)
    {
        // Ending a case (closing or cancelling it) is the Resolve capability's, whichever endpoint is used.
        var target = (dto.Status ?? string.Empty).Replace(" ", string.Empty);
        if ((target.Equals("Closed", StringComparison.OrdinalIgnoreCase) || target.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
            && !user.Has(Permissions.CasesResolve))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "You do not have permission to close or cancel cases.", requiredPermission = Permissions.CasesResolve });
        dto.UserId = CurrentUserId;
        await _caseService.UpdateCaseStatusAsync(id, dto);
        return Ok(new { message = "Status updated successfully." });
    }

    [HttpPut("{id:guid}/assign")]
    public async Task<IActionResult> AssignCase(Guid id, [FromBody] AssignCaseDto dto, [FromServices] ICurrentUserAccessor user)
    {
        dto.UserId = CurrentUserId;
        await _caseService.AssignCaseAsync(id, dto, user.Has(Permissions.CasesAssign));
        return Ok(new { message = "Case assigned successfully." });
    }

    /// <summary>Who could hold this case (its team's members and lead), with live status, open cases, capacity and why someone cannot.</summary>
    [HttpGet("{id:guid}/eligible-assignees")]
    public async Task<IActionResult> GetEligibleAssignees(Guid id, [FromServices] ICurrentUserAccessor user, CancellationToken ct)
    {
        return Ok(await _caseService.GetEligibleAssigneesAsync(id, user.Has(Permissions.CasesAssign), ct));
    }

    [HttpPost("{id:guid}/notes")]
    public async Task<IActionResult> AddNote(Guid id, [FromBody] AddNoteDto dto)
    {
        dto.UserId = CurrentUserId;
        await _caseService.AddNoteAsync(id, dto);
        return Ok(new { message = "Note added successfully." });
    }

    [HttpPost("{id:guid}/timeline-interaction")]
    public async Task<IActionResult> AddTimelineInteraction(Guid id, [FromBody] AddTimelineInteractionDto dto)
    {
        dto.UserId = CurrentUserId;
        await _caseService.AddTimelineInteractionAsync(id, dto, CurrentUserId);
        return Ok(new { message = dto.IsInternal ? "Internal note posted successfully." : "Customer reply sent successfully." });
    }

    /// <summary>Who a swarm can be requested from: every team's lead and the Supervisors (never regular agents).</summary>
    [HttpGet("{id:guid}/swarm/candidates")]
    public async Task<IActionResult> GetSwarmCandidates(Guid id, CancellationToken ct)
        => Ok(await _collaboration.GetSwarmCandidatesAsync(id, CurrentUserId, ct));

    [HttpPost("{id:guid}/swarm")]
    public async Task<IActionResult> RequestSwarm(Guid id, [FromBody] CreateSwarmRequestDto dto)
        => Ok(await _collaboration.RequestSwarmAsync(id, dto, CurrentUserId));

    /// <summary>The recipient accepts (joins as a collaborator) or declines a swarm request.</summary>
    [HttpPost("{id:guid}/swarm/{requestId:guid}/respond")]
    public async Task<IActionResult> RespondToSwarm(Guid id, Guid requestId, [FromBody] RespondSwarmRequestDto dto)
        => Ok(await _collaboration.RespondToSwarmAsync(id, requestId, dto, CurrentUserId));

    [HttpGet("{id:guid}/attachments")]
    public async Task<IActionResult> GetAttachments(Guid id, CancellationToken ct)
        => Ok(await _attachments.GetAttachmentsAsync(id, ct));

    [HttpPost("{id:guid}/attachments")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("uploads")]
    public async Task<IActionResult> UploadAttachment(Guid id, [FromForm] Microsoft.AspNetCore.Http.IFormFile file, [FromForm] string? note, CancellationToken ct)
    {
        var attachment = await _attachments.UploadAttachmentAsync(id, file, note, CurrentUserId, ct);
        await _caseService.MarkWorkStartedAsync(id, CurrentUserId, "a file was attached");
        return Ok(attachment);
    }

    /// <summary>Who an @mention on this case can name: its owner, co-workers, watchers and team lead (not the caller).</summary>
    [HttpGet("{id:guid}/mentionable-users")]
    public async Task<IActionResult> GetMentionableUsers(Guid id, [FromServices] IMentionService mentions, CancellationToken ct)
        => Ok(await mentions.GetMentionableAsync(id, CurrentUserId, ct));

    /// <summary>
    /// Always a DOWNLOAD, never rendered by the browser: the type comes from the verified extension, the browser is told not to
    /// guess otherwise (nosniff), and the response is not cached by shared caches.
    /// </summary>
    [HttpGet("{id:guid}/attachments/{attachmentId:guid}/download")]
    public async Task<IActionResult> DownloadAttachment(Guid id, Guid attachmentId, CancellationToken ct)
    {
        var download = await _attachments.OpenDownloadAsync(id, attachmentId, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        if (download.Sha256 != null) Response.Headers["X-Content-SHA256"] = download.Sha256;
        return File(download.Content, download.ContentType, download.FileName);   // File(...) sends Content-Disposition: attachment
    }

    /// <summary>Teams to choose collaborators from (Add Collaborator, step 1).</summary>
    [HttpGet("{id:guid}/collaborators/teams")]
    public async Task<IActionResult> GetCollaboratorTeams(Guid id, CancellationToken ct)
        => Ok(await _collaboration.GetCollaboratorTeamsAsync(id, ct));

    /// <summary>One team's members and lead (Add Collaborator, step 2).</summary>
    [HttpGet("{id:guid}/collaborators/candidates")]
    public async Task<IActionResult> GetCollaboratorCandidates(Guid id, [FromQuery] Guid teamId, CancellationToken ct)
        => Ok(await _collaboration.GetCollaboratorCandidatesAsync(id, teamId, CurrentUserId, ct));

    [HttpPost("{id:guid}/coworkers")]
    public async Task<IActionResult> AddCoworkers(Guid id, [FromBody] AddCollaboratorsDto dto)
    {
        await _collaboration.AddCoworkersAsync(id, dto, CurrentUserId);
        return Ok(new { message = "Collaborators added successfully." });
    }

    [HttpDelete("{id:guid}/coworkers/{coworkerId:guid}")]
    public async Task<IActionResult> RemoveCoworker(Guid id, Guid coworkerId)
    {
        await _collaboration.RemoveCoworkerAsync(id, coworkerId, CurrentUserId);
        return Ok(new { message = "Coworker removed successfully." });
    }

    /// <summary>Case Collaboration feed: collaborators plus collaboration-only activity, newest first.</summary>
    [HttpGet("{id:guid}/collaboration")]
    public async Task<IActionResult> GetCollaboration(Guid id, [FromQuery] DateTime? before, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var result = await _collaboration.GetCollaborationAsync(id, before, Math.Clamp(limit, 1, 200), ct);
        return Ok(result);
    }

    [HttpPost("{id:guid}/collaboration/notes")]
    public async Task<IActionResult> AddCollaborationNote(Guid id, [FromBody] AddCollaborationNoteDto dto)
    {
        var activity = await _collaboration.AddCollaborationNoteAsync(id, dto?.Content ?? string.Empty, CurrentUserId);
        await _caseService.MarkWorkStartedAsync(id, CurrentUserId, "a collaboration note was added");
        return Ok(activity);
    }

    [HttpPut("{id:guid}/transfer")]
    [RequirePermission(Permissions.CasesTransfer)]
    public async Task<IActionResult> TransferDepartment(Guid id, [FromBody] TransferDepartmentDto dto, [FromServices] ICurrentUserAccessor user)
    {
        await _caseService.TransferDepartmentAsync(id, dto, CurrentUserId, user.Has(Permissions.CasesAssign));
        return Ok(new { message = "Department transferred successfully." });
    }

    public sealed class CustomerNamesRequest
    {
        public List<Guid> CaseIds { get; set; } = new();
    }

    /// <summary>
    /// The Customer 360 customer's name on each of these cases, read live (a case stores only identifiers): { caseId: name }.
    /// Asked for separately, after a list has drawn, so a list never waits on Customer 360; a case it cannot name is left
    /// out and keeps its "Customer 360 · Individual" label. Only cases the caller may see are answered, at most 100.
    /// </summary>
    [HttpPost("customer-names")]
    [RequirePermission(Permissions.CasesRead)]   // a read, even though it is a POST
    public async Task<IActionResult> GetCustomerNames([FromBody] CustomerNamesRequest request,
        [FromServices] ICaseCustomerNames customerNames, [FromServices] ICaseAccessScope access,
        [FromServices] CaseManagement.Api.Data.AppDbContext db, CancellationToken ct)
    {
        var ids = (request.CaseIds ?? new()).Distinct().Take(100).ToList();
        if (ids.Count == 0) return Ok(new Dictionary<Guid, string>());

        var visible = await access.Apply(db.Cases.AsNoTracking())
            .Where(c => ids.Contains(c.Id) && c.CustomerSource == CaseManagement.Api.Models.CustomerSources.Customer360)
            .Select(c => c.Id).ToListAsync(ct);
        var rows = visible.Select(id => new NameRow { Id = id }).ToList();
        await customerNames.FillAsync(rows, r => r.Id, _ => CaseManagement.Api.Models.CustomerSources.Customer360, r => r.Name, (r, n) => r.Name = n, ct);
        return Ok(rows.Where(r => !string.IsNullOrEmpty(r.Name)).ToDictionary(r => r.Id, r => r.Name!));
    }

    private sealed class NameRow
    {
        public Guid Id { get; init; }
        public string? Name { get; set; }
    }

    /// <summary>One customer's cases (the selected customer on Create Case, or any customer), newest first.</summary>
    [HttpPost("for-customer")]
    [RequirePermission(Permissions.CasesRead)]   // a read, even though it is a POST
    public async Task<IActionResult> GetCasesForCustomer([FromBody] CustomerCasesQueryDto query, CancellationToken ct)
        => Ok(await _caseService.GetCasesForCustomerAsync(query, ct));

    [HttpGet("{id:guid}/related-customer-cases")]
    public async Task<IActionResult> GetRelatedCustomerCases(Guid id)
    {
        var cases = await _caseService.GetRelatedCustomerCasesAsync(id);
        return Ok(cases);
    }

    [HttpPost("{id:guid}/link")]
    public async Task<IActionResult> LinkCase(Guid id, [FromBody] LinkCaseDto dto)
    {
        dto.UserId = CurrentUserId;
        var result = await _caseService.LinkCaseAsync(id, dto);
        return Ok(result);
    }

    [HttpPost("{id:guid}/unlink")]
    public async Task<IActionResult> UnlinkCase(Guid id, [FromBody] UnlinkCaseDto dto)
    {
        dto.UserId = CurrentUserId;
        await _caseService.UnlinkCaseAsync(id, dto);
        return Ok(new { message = "Case unlinked successfully." });
    }

    [HttpPut("{id:guid}/resolve")]
    [RequirePermission(Permissions.CasesResolve)]
    public async Task<IActionResult> ResolveCase(Guid id, [FromBody] ResolveCaseDto dto)
    {
        dto.UserId = CurrentUserId;
        await _caseService.ResolveCaseAsync(id, dto);
        return Ok(new { message = "Case resolved successfully." });
    }

    [HttpPut("{id:guid}/reopen")]
    [RequirePermission(Permissions.CasesResolve)]
    public async Task<IActionResult> ReopenCase(Guid id, [FromBody] ReopenCaseDto dto)
    {
        dto.UserId = CurrentUserId;
        var result = await _caseService.ReopenCaseAsync(id, dto);
        return Ok(result);
    }

    [HttpPost("{id:guid}/escalate")]
    public async Task<IActionResult> EscalateCase(Guid id, [FromBody] EscalateCaseDto dto)
    {
        dto.UserId = CurrentUserId;
        await _caseService.EscalateCaseAsync(id, dto, CurrentUserId);
        return Ok(new { message = "Case escalated successfully." });
    }

    /// <summary>The levels this case can be escalated to and, for each, who may receive it.</summary>
    [HttpGet("{id:guid}/escalation-options")]
    public async Task<IActionResult> GetEscalationOptions(Guid id, CancellationToken ct)
        => Ok(await _caseService.GetEscalationOptionsAsync(id, CurrentUserId, ct));

    [HttpGet("escalation-matrix")]
    public async Task<IActionResult> GetEscalationMatrix([FromQuery] Guid? caseId, CancellationToken ct)
    {
        var matrix = await _caseService.GetEscalationMatrixConfigAsync(caseId, ct);
        return Ok(matrix);
    }

    [HttpGet("audit")]
    public async Task<IActionResult> GetAuditLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] string? actionType = null, [FromQuery] string? search = null, CancellationToken ct = default)
    {
        var logs = await _caseService.GetCaseAuditEventsAsync(new AuditFilter(actionType, search), page, pageSize, ct);
        return Ok(logs);
    }

    /// <summary>
    /// Paginated timeline events for the case detail view.
    /// Returns up to <paramref name="limit"/> events older than <paramref name="before"/>.
    /// </summary>
    [HttpGet("{id:guid}/timeline")]
    [HttpGet("{id:guid}/events")]
    public async Task<IActionResult> GetCaseTimeline(
        Guid id,
        [FromQuery] DateTime? before,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var result = await _caseService.GetCaseTimelineEventsAsync(id, before, Math.Clamp(limit, 1, 200), ct);
        return Ok(result);
    }
}
