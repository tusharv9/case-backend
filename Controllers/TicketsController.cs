namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services.EmailIntake;
using CaseManagement.Api.Services.Tickets;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Tickets: requests (emails in a Tickets-mode mailbox, or raised by hand) waiting for a person to assign them to a team or
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

    /// <summary>The mailboxes (queues) the caller may see and raise tickets in, with each one's suggested case details.</summary>
    [HttpGet("mailboxes")]
    public async Task<IActionResult> Mailboxes(CancellationToken ct) => Ok(await admin.AccessibleMailboxesAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await tickets.GetAsync(id, ct));

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

    /// <summary>Raise a ticket by hand (multipart form: the fields of <see cref="CreateTicketDto"/> and any <c>files</c>).</summary>
    [HttpPost]
    [RequirePermission(Permissions.TicketsCreate)]
    [RequestSizeLimit(260L * 1024 * 1024)]
    public async Task<IActionResult> Create([FromForm] CreateTicketDto dto, [FromForm] List<IFormFile>? files, CancellationToken ct) =>
        Ok(await tickets.CreateAsync(dto, files ?? new List<IFormFile>(), CurrentUserId, ct));

    /// <summary>Assign the ticket: it becomes a case with the details given, held by the chosen agent (or the team's rule picks one).</summary>
    [HttpPost("{id:guid}/assign")]
    public async Task<IActionResult> Assign(Guid id, [FromBody] AssignTicketDto dto, CancellationToken ct) =>
        Ok(await tickets.AssignAsync(id, dto, CurrentUserId, user.Has(Permissions.CasesAssign), ct));

    /// <summary>Close the ticket without a case (spam, duplicate, nothing to do), with the reason.</summary>
    [HttpPost("{id:guid}/close")]
    public async Task<IActionResult> Close(Guid id, [FromBody] CloseTicketDto dto, CancellationToken ct) =>
        Ok(await tickets.CloseAsync(id, dto, CurrentUserId, ct));
}
