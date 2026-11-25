namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services.EmailIntake;
using CaseManagement.Api.Services.Tickets;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Tickets: requests (emails from registered senders, or approved by a reviewer) waiting for a person to assign them to a team or
/// agent, which makes each a case. Everything is scoped to the mailboxes the caller's teams are linked to.
/// </summary>
[ApiController]
[Route("api/tickets")]
[RequirePermission(Permissions.TicketsView, Permissions.TicketsAssign)]
public class TicketsController(TicketService tickets, EmailIntakeAdminService admin, ICurrentUserAccessor user) : BaseApiController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? search, [FromQuery] Guid? mailboxId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(await tickets.ListAsync(status, search, mailboxId, page, pageSize, ct));

    [HttpGet("counts")]
    public async Task<IActionResult> Counts([FromQuery] Guid? mailboxId, CancellationToken ct) => Ok(await tickets.CountsAsync(mailboxId, ct));

    /// <summary>The mailboxes (queues) the caller may see, with each one's suggested case details.</summary>
    [HttpGet("mailboxes")]
    public async Task<IActionResult> Mailboxes(CancellationToken ct) => Ok(await admin.AccessibleMailboxesAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await tickets.GetAsync(id, ct, CurrentUserId));

    /// <summary>
    /// Claims the ticket while the caller works on it (opening its Assign or Close form), or extends their claim; 409 names who is
    /// processing it. <paramref name="takeOver"/>: a Supervisor takes someone else's claim.
    /// </summary>
    [HttpPost("{id:guid}/claim")]
    public async Task<IActionResult> Claim(Guid id, [FromQuery] bool takeOver, CancellationToken ct) =>
        Ok(await tickets.ClaimAsync(id, CurrentUserId, takeOver, user.Has(Permissions.CasesSupervise), ct));

    /// <summary>Lets go of the caller's claim (the form was closed).</summary>
    [HttpPost("{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct)
    {
        await tickets.ReleaseAsync(id, CurrentUserId, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> Attachment(Guid id, Guid attachmentId, CancellationToken ct)
    {
        var (content, name, type) = await tickets.OpenAttachmentAsync(id, attachmentId, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        // Always a download, never shown inline: the file came from outside.
        return File(content, "application/octet-stream", name);
    }

    /// <summary>Who could take the case in the team of <paramref name="departmentId"/>, with the reason anyone cannot.</summary>
    [HttpGet("assignees")]
    public async Task<IActionResult> Assignees([FromQuery] Guid departmentId, CancellationToken ct) => Ok(await tickets.AssigneesAsync(departmentId, ct));

    /// <summary>How a case of <paramref name="departmentId"/> made from this ticket would be given out (its team, the algorithm, whether an agent must be chosen).</summary>
    [HttpGet("{id:guid}/assignment-rule")]
    public async Task<IActionResult> AssignmentRule(Guid id, [FromQuery] Guid departmentId, CancellationToken ct) =>
        Ok(await tickets.AssignmentRuleAsync(id, departmentId, ct));

    /// <summary>
    /// Raising a ticket by hand was retired: tickets come only from email. Kept for one release so an old screen gets a clear
    /// answer instead of a 404 or 405; remove it in the release after.
    /// </summary>
    [HttpPost]
    public IActionResult Create() => Problem(statusCode: StatusCodes.Status410Gone, title: "Tickets can no longer be raised by hand",
        detail: "Tickets are opened only by email sent to a company mailbox. Reload Case Management to get the current screen.");

    /// <summary>Assign the ticket: it becomes a case with the details given, held by the chosen agent (or the team's rule picks one).</summary>
    [HttpPost("{id:guid}/assign")]
    public async Task<IActionResult> Assign(Guid id, [FromBody] AssignTicketDto dto, CancellationToken ct) =>
        Ok(await tickets.AssignAsync(id, dto, CurrentUserId, user.Has(Permissions.CasesAssign), ct));

    /// <summary>Close the ticket without a case (spam, duplicate, nothing to do), with the reason.</summary>
    [HttpPost("{id:guid}/close")]
    public async Task<IActionResult> Close(Guid id, [FromBody] CloseTicketDto dto, CancellationToken ct) =>
        Ok(await tickets.CloseAsync(id, dto, CurrentUserId, ct));
}
