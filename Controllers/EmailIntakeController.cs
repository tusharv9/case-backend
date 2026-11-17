namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.EmailIntake;
using Microsoft.AspNetCore.Mvc;

/// <summary>The Inbox: emails read from the configured mailboxes, and what each became.</summary>
[ApiController]
[Route("api/email-intake")]
[RequirePermission(Permissions.EmailInboxView, Permissions.EmailInboxView)]
public class EmailIntakeController(EmailIntakeAdminService admin, EmailIntakeProcessor processor, ICurrentUserAccessor user) : BaseApiController
{
    [HttpGet("inbox")]
    public async Task<IActionResult> List([FromQuery] string? filter, [FromQuery] string? search, [FromQuery] Guid? mailboxId, [FromQuery] Guid? organisationId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(await admin.ListAsync(filter, search, mailboxId, page, pageSize, ct, organisationId));

    [HttpGet("inbox/counts")]
    public async Task<IActionResult> Counts([FromQuery] Guid? mailboxId, [FromQuery] Guid? organisationId, CancellationToken ct) =>
        Ok(await admin.CountsAsync(mailboxId, ct, organisationId));

    /// <summary>The organisations the caller's emails come from (the Inbox's organisation filter).</summary>
    [HttpGet("organisations")]
    public async Task<IActionResult> Organisations(CancellationToken ct) => Ok(await admin.InboxOrganisationsAsync(ct));

    /// <summary>The mailboxes the caller may see (for the mailbox filter); no connection details.</summary>
    [HttpGet("mailboxes")]
    public async Task<IActionResult> Mailboxes(CancellationToken ct) => Ok(await admin.AccessibleMailboxesAsync(ct));

    [HttpGet("emails/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await admin.GetAsync(id, ct));

    /// <summary>The email exactly as it arrived (.eml), for opening in a mail program.</summary>
    [HttpGet("emails/{id:guid}/original")]
    public async Task<IActionResult> Original(Guid id, CancellationToken ct)
    {
        var (content, name) = await admin.OpenOriginalAsync(id, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(content, "message/rfc822", name);
    }

    /// <summary>
    /// Decides on an email from a sender who is not registered: approve this one email into Tickets (optionally also registering the
    /// sender, with its own confirmation and permission), reject it, or mark it suspicious.
    /// </summary>
    [HttpPost("emails/{id:guid}/review")]
    [RequirePermission(Permissions.EmailReview)]
    public async Task<IActionResult> Review(Guid id, [FromBody] ReviewEmailDto dto, CancellationToken ct)
    {
        var detail = await admin.ReviewAsync(id, dto, CurrentUserId, user.Has(Permissions.EmailSendersRegister), ct);
        if (detail.ReviewDecision == ReviewDecisions.Approved && detail.Status == EmailStatuses.Received)
        {
            await processor.ProcessAsync(id, manual: true, ct);   // opens its ticket now; the worker would also pick it up
            detail = await admin.GetAsync(id, ct);
        }
        return Ok(detail);
    }

    /// <summary>
    /// "Check again": works the email through once more — e.g. after its sender's domain was verified, or a case detail fixed. An
    /// email that already has its case is returned unchanged, so pressing twice never makes two cases.
    /// </summary>
    [HttpPost("emails/{id:guid}/recheck")]
    [RequirePermission(Permissions.EmailManage)]
    public async Task<IActionResult> Recheck(Guid id, CancellationToken ct)
    {
        var detail = await admin.GetAsync(id, ct);
        if (!detail.CanRecheck) return Ok(detail);
        await processor.ProcessAsync(id, manual: true, ct);
        return Ok(await admin.GetAsync(id, ct));
    }
}

/// <summary>Maintenance: the mailboxes read for new tickets, and the verified sender domains.</summary>
[ApiController]
[Route("api/email-maintenance")]
[RequirePermission(Permissions.EmailManage)]
public class EmailMaintenanceController(EmailIntakeAdminService admin, EmailIntakeProcessor processor) : BaseApiController
{
    [HttpGet("mailboxes")]
    public async Task<IActionResult> Mailboxes(CancellationToken ct) => Ok(await admin.MailboxesAsync(ct));

    [HttpPost("mailboxes")]
    public async Task<IActionResult> CreateMailbox([FromBody] SaveMailboxDto dto, CancellationToken ct) => Ok(await admin.CreateMailboxAsync(dto, CurrentUserId, ct));

    [HttpPut("mailboxes/{id:guid}")]
    public async Task<IActionResult> UpdateMailbox(Guid id, [FromBody] SaveMailboxDto dto, CancellationToken ct) => Ok(await admin.UpdateMailboxAsync(id, dto, CurrentUserId, ct));

    [HttpDelete("mailboxes/{id:guid}")]
    public async Task<IActionResult> DeleteMailbox(Guid id, CancellationToken ct)
    {
        await admin.DeleteMailboxAsync(id, CurrentUserId, ct);
        return NoContent();
    }

    /// <summary>Signs in to the mail server (IMAP and SMTP) with the settings given, without saving them.</summary>
    [HttpPost("mailboxes/test")]
    public async Task<IActionResult> Test([FromBody] TestMailboxDto dto, CancellationToken ct) => Ok(await admin.TestAsync(dto, ct));

    /// <summary>Reads the mailbox now instead of waiting for the next cycle, then works through what arrived.</summary>
    [HttpPost("mailboxes/{id:guid}/sync")]
    public async Task<IActionResult> Sync(Guid id, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var read = await processor.SyncMailboxAsync(id, ct);
        await processor.ProcessPendingAsync(ct);
        var outcome = await admin.SyncOutcomeAsync(id, startedAt, ct);
        return Ok(new
        {
            read,
            ticketsCreated = outcome.GetValueOrDefault(EmailStatuses.TicketCreated),
            linkedToTicket = outcome.GetValueOrDefault(EmailStatuses.LinkedToTicket),
            casesCreated = outcome.GetValueOrDefault(EmailStatuses.CaseCreated),
            linkedToCase = outcome.GetValueOrDefault(EmailStatuses.LinkedToCase),
            notVerified = outcome.GetValueOrDefault(EmailStatuses.NotVerified),
            ignored = outcome.GetValueOrDefault(EmailStatuses.Ignored),
            failed = outcome.GetValueOrDefault(EmailStatuses.Failed),
            mailbox = (await admin.MailboxesAsync(ct)).FirstOrDefault(m => m.Id == id),
        });
    }

    [HttpGet("domains")]
    public async Task<IActionResult> Domains(CancellationToken ct) => Ok(await admin.DomainsAsync(ct));

    [HttpPost("domains")]
    public async Task<IActionResult> CreateDomain([FromBody] SaveVerifiedDomainDto dto, CancellationToken ct) => Ok(await admin.SaveDomainAsync(null, dto, CurrentUserId, ct));

    [HttpPut("domains/{id:guid}")]
    public async Task<IActionResult> UpdateDomain(Guid id, [FromBody] SaveVerifiedDomainDto dto, CancellationToken ct) => Ok(await admin.SaveDomainAsync(id, dto, CurrentUserId, ct));

    [HttpDelete("domains/{id:guid}")]
    public async Task<IActionResult> DeleteDomain(Guid id, CancellationToken ct)
    {
        await admin.DeleteDomainAsync(id, CurrentUserId, ct);
        return NoContent();
    }

    /// <summary>Every registered sender, with its organisation, domain, mailbox and team.</summary>
    [HttpGet("senders")]
    public async Task<IActionResult> Senders(CancellationToken ct) => Ok(await admin.SendersAsync(ct));

    /// <summary>Registers one sender directly; its domain is registered for the organisation when it is not yet.</summary>
    [HttpPost("senders")]
    public async Task<IActionResult> CreateSender([FromBody] SaveSenderDto dto, CancellationToken ct) => Ok(await admin.SaveSenderAsync(null, dto, CurrentUserId, ct));

    [HttpPut("senders/{id:guid}")]
    public async Task<IActionResult> UpdateSender(Guid id, [FromBody] SaveSenderDto dto, CancellationToken ct) => Ok(await admin.SaveSenderAsync(id, dto, CurrentUserId, ct));

    [HttpDelete("senders/{id:guid}")]
    public async Task<IActionResult> DeleteSender(Guid id, CancellationToken ct)
    {
        await admin.DeleteSenderAsync(id, CurrentUserId, ct);
        return NoContent();
    }

    /// <summary>The organisations verified domains belong to (for the domain form's organisation field).</summary>
    [HttpGet("organisations")]
    public async Task<IActionResult> Organisations(CancellationToken ct) => Ok(await admin.OrganisationsAsync(ct));

    /// <summary>The teams, departments (with sub-categories), case types, priorities and languages a mailbox's tickets can suggest.</summary>
    [HttpGet("case-options")]
    public async Task<IActionResult> CaseOptions(CancellationToken ct) => Ok(await admin.CaseOptionsAsync(ct));
}
