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
        [FromQuery] Guid? teamId,
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
            status, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? Paging.DefaultPageSize, 1, Paging.MaxPageSize),
            CrossTeamFilter(access, teamId), caseType, search, priority, channel, ct);
        return Ok(paged);
    }

    /// <summary>Open / SLA-breached counts for the Case Management header.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetCaseStats([FromQuery] Guid? teamId, [FromServices] ICaseAccessScope access, CancellationToken ct)
    {
        var stats = await _caseService.GetCaseStatsAsync(CrossTeamFilter(access, teamId), ct);
        return Ok(stats);
    }

    /// <summary>
    /// Filtering by team is for people who see every team's cases (cases:ViewAll, administrators). Everyone else already sees
    /// only their own teams' and assignments' cases, and a team filter is ignored for them — it could only narrow that set in
    /// ways the screen does not offer them.
    /// </summary>
    internal static Guid? CrossTeamFilter(ICaseAccessScope access, Guid? teamId) => access.SeesAll ? teamId : null;

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetCaseDetails(Guid id, [FromServices] AppDbContext db, [FromServices] ILogger<CasesController> logger, CancellationToken ct)
    {
        var caseDetails = await _caseService.GetCaseDetailsAsync(id, ct);
        if (caseDetails == null) return NotFound();
        // Only a case the caller may see reaches this line (the case-access filter answers 404 otherwise).
        await CaseViewLog.RecordAsync(db, id, CurrentUserId, logger, ct);
        return Ok(caseDetails);
    }

    /// <summary>Create Case → Assign to: who in the department's team could take a new case, with the reason anyone cannot.</summary>
    [HttpGet("department-assignees")]
    [RequirePermission(Permissions.CasesCreate)]
    public async Task<IActionResult> DepartmentAssignees([FromQuery] Guid departmentId, CancellationToken ct) =>
        Ok(await _caseService.GetDepartmentAssigneesAsync(departmentId, ct));

    /// <summary>How a new case of this department and source channel would be given out (team, algorithm, whether an agent must be chosen).</summary>
    [HttpGet("assignment-rule")]
    [RequirePermission(Permissions.CasesCreate)]
    public async Task<IActionResult> AssignmentRule([FromQuery] Guid departmentId, [FromQuery] string? sourceChannel, CancellationToken ct) =>
        Ok(await _caseService.GetAssignmentRuleAsync(departmentId, sourceChannel, ct));

    [HttpPost]
    [RequirePermission(Permissions.CasesCreate)]
    public async Task<IActionResult> CreateCase([FromBody] CreateCaseDto dto, [FromServices] ICurrentUserAccessor user, [FromServices] AppDbContext db,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey = null)
    {
        // The same submission twice (a double click, a retry after a timeout, a second tab sending the same form) makes one case:
        // the key the form sends is stored with the case, in its transaction, and a repeat is answered with the case already made.
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (key is { Length: > 100 }) return BadRequest(new { error = "The Idempotency-Key header is at most 100 characters." });
        if (key != null && await ExistingForKeyAsync(db, key) is { } earlier) return earlier;

        Models.Case newCase;
        try
        {
            newCase = await _caseService.CreateCaseAsync(dto, CurrentUserId, key == null ? null : async created =>
            {
                db.CaseCreationRequests.Add(new Models.CaseCreationRequest { Key = key, CaseId = created.Id, CreatedById = CurrentUserId });
                await db.SaveChangesAsync();
            }, callerMayAssignAnyCase: user.Has(Permissions.CasesAssign));
        }
        catch (DbUpdateException ex) when (key != null && ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Two copies of the submission arrived together: the other one made the case (this one rolled back).
            db.ChangeTracker.Clear();
            if (await ExistingForKeyAsync(db, key) is { } winner) return winner;
            throw;
        }
        if (key != null && db.Database.IsRelational())
            await db.CaseCreationRequests.Where(r => r.CreatedAt < DateTime.UtcNow.AddDays(-1)).ExecuteDeleteAsync();   // kept a day
        // The same shape every screen reads a case in (owner, team and creator named, SLA verdict, masking applied), so the
        // list can show the new case straight away. A Customer 360 customer's name is not stored; the form that chose them has it.
        var created = await _caseService.GetCaseDetailsAsync(newCase.Id);
        return CreatedAtAction(nameof(GetCaseDetails), new { id = newCase.Id }, (object?)created ?? new { id = newCase.Id, caseNumber = newCase.CaseNumber });
    }

    /// <summary>The case an earlier submission with this key made, as Create Case answers it (200 instead of 201), or null.</summary>
    private async Task<IActionResult?> ExistingForKeyAsync(AppDbContext db, string key)
    {
        var earlier = await db.CaseCreationRequests.AsNoTracking().Where(r => r.Key == key).Select(r => new { r.CaseId, r.CreatedById }).FirstOrDefaultAsync();
        if (earlier == null) return null;
        if (earlier.CreatedById != CurrentUserId) return Conflict(new { error = "This form was already submitted by someone else. Open Create Case again." });
        var details = await _caseService.GetCaseDetailsAsync(earlier.CaseId);
        return Ok((object?)details ?? new { id = earlier.CaseId });
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

    /// <summary>Handle: the person holding an Open case starts working on it (Open → In Progress). 403 for anyone else, 409 if already started.</summary>
    [HttpPost("{id:guid}/handle")]
    public async Task<IActionResult> Handle(Guid id)
    {
        await _caseService.HandleCaseAsync(id, CurrentUserId);
        return Ok(await _caseService.GetCaseDetailsAsync(id));
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
    public async Task<IActionResult> AddTimelineInteraction(Guid id, [FromBody] AddTimelineInteractionDto dto,
        [FromServices] Services.EmailIntake.CaseEmailSender emails, [FromServices] Data.AppDbContext db, CancellationToken ct)
    {
        dto.UserId = CurrentUserId;
        await _caseService.AddTimelineInteractionAsync(id, dto, CurrentUserId);
        if (dto.IsInternal) return Ok(new { message = "Internal note posted successfully." });

        // An emailed reply is sent now; a failure is kept on the email and retried, never lost.
        await emails.SendForCaseAsync(id, ct);
        var email = await db.CaseEmailMessages.AsNoTracking().Where(m => m.CaseId == id).OrderByDescending(m => m.CreatedAt).FirstOrDefaultAsync(ct);
        if (email == null || email.CreatedAt < DateTime.UtcNow.AddMinutes(-2))
            return Ok(new { message = "Customer reply recorded." });
        return Ok(new
        {
            message = email.Status == Models.CaseEmailStatuses.Sent ? $"Reply emailed to {email.ToAddress}." : $"Reply saved; the email to {email.ToAddress} could not be sent yet and will be retried.",
            email = new { email.Id, email.Status, email.ToAddress, email.Error, email.SentAt },
        });
    }

    /// <summary>The Customer Reply box: where an emailed reply goes (recipient, subject, mailbox or mailboxes), or why it cannot be emailed.</summary>
    [HttpGet("{id:guid}/reply-options")]
    public async Task<IActionResult> ReplyOptions(Guid id, [FromServices] Data.AppDbContext db, CancellationToken ct)
    {
        _ = await _caseService.GetCaseDetailsAsync(id) ?? throw new KeyNotFoundException("Case not found.");   // only a case the caller may see
        var c = await db.Cases.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        return Ok(await Services.EmailIntake.CaseEmailOutbox.OptionsAsync(db, c, ct));
    }

    /// <summary>Sends a Customer Reply email that failed again, now.</summary>
    [HttpPost("{id:guid}/emails/{messageId:guid}/retry")]
    public async Task<IActionResult> RetryEmail(Guid id, Guid messageId, [FromServices] Services.EmailIntake.CaseEmailSender emails, CancellationToken ct)
    {
        _ = await _caseService.GetCaseDetailsAsync(id) ?? throw new KeyNotFoundException("Case not found.");   // only a case the caller may see
        var email = await emails.RetryAsync(id, messageId, ct);
        return Ok(new { email.Id, email.Status, email.ToAddress, email.Error, email.SentAt });
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

    /// <summary>
    /// The emails received on this case (the one that opened it and every reply), oldest first, each with the timeline entry it
    /// belongs to. Entries written before entries were linked to their email are matched by subject and time.
    /// </summary>
    [HttpGet("{id:guid}/emails")]
    public async Task<IActionResult> GetEmails(Guid id, [FromServices] Data.AppDbContext db, CancellationToken ct)
    {
        var emails = await db.InboundEmails.AsNoTracking().Where(e => e.CaseId == id).OrderBy(e => e.ReceivedAt)
            .Select(e => new { e.Id, e.FromAddress, e.FromName, e.Subject, e.ReceivedAt, e.Status, HasOriginal = e.RawKey != null })
            .ToListAsync(ct);
        var events = await db.CaseEvents.AsNoTracking()
            .Where(ev => ev.CaseId == id && ev.ActionType == "EMAIL_REPLY_RECEIVED")
            .Select(ev => new { ev.Id, ev.InboundEmailId, ev.Message, ev.CreatedAt }).ToListAsync(ct);

        var used = new HashSet<Guid>();
        var result = emails.Select((e, index) =>
        {
            var linked = events.FirstOrDefault(ev => ev.InboundEmailId == e.Id)
                ?? events.Where(ev => ev.InboundEmailId == null && !used.Contains(ev.Id)
                        && ev.Message.Contains($": {e.Subject}", StringComparison.Ordinal)
                        && Math.Abs((ev.CreatedAt - e.ReceivedAt).TotalMinutes) < 30)
                    .OrderBy(ev => Math.Abs((ev.CreatedAt - e.ReceivedAt).TotalSeconds)).FirstOrDefault();
            if (linked != null) used.Add(linked.Id);
            return new
            {
                e.Id, e.FromAddress, e.FromName, e.Subject, e.ReceivedAt, e.HasOriginal,
                // The email that opened the case (directly or through its ticket) has no reply entry: it is the case's description.
                IsOriginal = linked == null && index == 0,
                EventId = linked?.Id,
            };
        }).ToList();
        return Ok(result);
    }

    /// <summary>One received email as sent: its HTML (signature, footer and pictures) made safe, or its text.</summary>
    [HttpGet("{id:guid}/emails/{emailId:guid}/body")]
    public async Task<IActionResult> GetEmailBody(Guid id, Guid emailId, [FromServices] Data.AppDbContext db,
        [FromServices] Services.EmailIntake.IEmailBodyRenderer renderer, CancellationToken ct)
    {
        // The email must belong to THIS case (the case itself was already checked by [CaseAccess]).
        var email = await db.InboundEmails.AsNoTracking().FirstOrDefaultAsync(e => e.Id == emailId && e.CaseId == id, ct)
                    ?? throw new KeyNotFoundException("Email not found.");
        Response.Headers["Cache-Control"] = "private, no-store";
        return Ok(await renderer.RenderAsync(email, ct));
    }

    [HttpGet("{id:guid}/attachments")]
    public async Task<IActionResult> GetAttachments(Guid id, [FromServices] ICaseAccessScope access, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(id, ct)) throw new KeyNotFoundException("Case not found.");   // only a case the caller may see
        return Ok(await _attachments.GetAttachmentsAsync(id, ct));
    }

    [HttpPost("{id:guid}/attachments")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("uploads")]
    public async Task<IActionResult> UploadAttachment(Guid id, [FromForm] Microsoft.AspNetCore.Http.IFormFile file, [FromForm] string? note,
        [FromServices] ICaseAccessScope access, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(id, ct)) throw new KeyNotFoundException("Case not found.");
        await _caseService.EnsureWorkMayStartAsync(id, CurrentUserId);
        var attachment = await _attachments.UploadAttachmentAsync(id, file, note, CurrentUserId, ct);
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
    public async Task<IActionResult> DownloadAttachment(Guid id, Guid attachmentId, [FromServices] ICaseAccessScope access, CancellationToken ct)
    {
        // Only a case the caller may see: an attachment id under another team's case is "not found".
        if (!await access.CanSeeAsync(id, ct)) throw new KeyNotFoundException("Attachment not found.");
        var download = await _attachments.OpenDownloadAsync(id, attachmentId, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        if (download.Sha256 != null) Response.Headers["X-Content-SHA256"] = download.Sha256;
        return File(download.Content, download.ContentType, download.FileName);   // File(...) sends Content-Disposition: attachment
    }

    /// <summary>
    /// Shown in the case instead of downloaded — for pictures and PDFs only (never SVG or HTML, which can carry script), typed from
    /// the verified extension, sandboxed, not sniffed and not cached. Anything else answers 415: download it instead.
    /// </summary>
    [HttpGet("{id:guid}/attachments/{attachmentId:guid}/preview")]
    public async Task<IActionResult> PreviewAttachment(Guid id, Guid attachmentId, [FromServices] ICaseAccessScope access, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(id, ct)) throw new KeyNotFoundException("Attachment not found.");
        var download = await _attachments.OpenDownloadAsync(id, attachmentId, ct);
        if (Services.AttachmentService.PreviewTypeOf(download.FileName) is not { } type)
        {
            await download.Content.DisposeAsync();
            return Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "This kind of file cannot be previewed",
                detail: "Pictures and PDFs can be previewed; download other files instead.");
        }
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        Response.Headers["Content-Disposition"] = "inline";
        return File(download.Content, type);
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
        await _caseService.EnsureWorkMayStartAsync(id, CurrentUserId);
        var activity = await _collaboration.AddCollaborationNoteAsync(id, dto?.Content ?? string.Empty, CurrentUserId);
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

    /// <summary>The case's escalations, manual and automatic, newest first.</summary>
    [HttpGet("{id:guid}/escalations")]
    public async Task<IActionResult> GetEscalationHistory(Guid id, CancellationToken ct)
        => Ok(await _caseService.GetEscalationHistoryAsync(id, ct));

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
