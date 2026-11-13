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
public class EmailIntakeController(EmailIntakeAdminService admin, EmailIntakeProcessor processor) : BaseApiController
{
    [HttpGet("inbox")]
    public async Task<IActionResult> List([FromQuery] string? filter, [FromQuery] string? search, [FromQuery] Guid? mailboxId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(await admin.ListAsync(filter, search, mailboxId, page, pageSize, ct));

    [HttpGet("inbox/counts")]
    public async Task<IActionResult> Counts([FromQuery] Guid? mailboxId, CancellationToken ct) => Ok(await admin.CountsAsync(mailboxId, ct));

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

/// <summary>Maintenance: the mailboxes read for new cases and the sender domains whose emails become cases.</summary>
[ApiController]
[Route("api/email-maintenance")]
[RequirePermission(Permissions.EmailManage)]
public class EmailMaintenanceController(EmailIntakeAdminService admin, EmailIntakeProcessor processor) : BaseApiController
{
    [HttpGet("mailboxes")]
    public async Task<IActionResult> Mailboxes(CancellationToken ct) => Ok(await admin.MailboxesAsync(ct));

    [HttpPost("mailboxes")]
    public async Task<IActionResult> CreateMailbox([FromBody] SaveMailboxDto dto, CancellationToken ct) => Ok(await admin.CreateMailboxAsync(dto, ct));

    [HttpPut("mailboxes/{id:guid}")]
    public async Task<IActionResult> UpdateMailbox(Guid id, [FromBody] SaveMailboxDto dto, CancellationToken ct) => Ok(await admin.UpdateMailboxAsync(id, dto, ct));

    [HttpDelete("mailboxes/{id:guid}")]
    public async Task<IActionResult> DeleteMailbox(Guid id, CancellationToken ct)
    {
        await admin.DeleteMailboxAsync(id, ct);
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
    public async Task<IActionResult> CreateDomain([FromBody] SaveVerifiedDomainDto dto, CancellationToken ct) => Ok(await admin.SaveDomainAsync(null, dto, ct));

    [HttpPut("domains/{id:guid}")]
    public async Task<IActionResult> UpdateDomain(Guid id, [FromBody] SaveVerifiedDomainDto dto, CancellationToken ct) => Ok(await admin.SaveDomainAsync(id, dto, ct));

    [HttpDelete("domains/{id:guid}")]
    public async Task<IActionResult> DeleteDomain(Guid id, CancellationToken ct)
    {
        await admin.DeleteDomainAsync(id, ct);
        return NoContent();
    }

    /// <summary>The case types, departments (with sub-categories), priorities and languages a verified domain can use.</summary>
    [HttpGet("case-options")]
    public async Task<IActionResult> CaseOptions(CancellationToken ct) => Ok(await admin.CaseOptionsAsync(ct));
}
