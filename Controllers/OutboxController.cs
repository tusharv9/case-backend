namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using Microsoft.AspNetCore.Mvc;
using OmniConnect.Outbox;

/// <summary>
/// For whoever runs Case Management: what its outbox still holds (notifications on their way to the Notification Service,
/// audit rows to the Audit Service), and a way to send refused rows again once the cause is fixed. The platform's other
/// producers expose the same under <c>internal/outbox</c>; Case Management has no internal key of its own to receive with, so
/// it is offered to the people who manage its configuration instead.
/// </summary>
[ApiController]
[Route("api/outbox")]
[RequirePermission(Permissions.ConfigManage)]
public class OutboxController(OutboxAdmin<AppDbContext> audit, OutboxAdmin<NotificationOutboxDbContext> notifications) : ControllerBase
{
    /// <summary>Both outboxes: <c>AuditOutboxMessages</c> and <c>NotificationOutboxMessages</c>, each per topic.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<OutboxSummary>> Summary(CancellationToken ct) =>
        Ok(new OutboxSummary([.. (await notifications.SummaryAsync(ct)).Topics, .. (await audit.SummaryAsync(ct)).Topics]));

    /// <summary>Sends one refused row again, whichever outbox holds it.</summary>
    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct) =>
        await notifications.RetryAsync(id, ct) || await audit.RetryAsync(id, ct) ? NoContent() : NotFound();

    /// <summary>Sends every refused row again: of one topic (<c>notification</c> or <c>audit</c>), or of both.</summary>
    [HttpPost("retry-rejected")]
    public async Task<IActionResult> RetryRejected([FromQuery] string? topic, CancellationToken ct)
    {
        var requeued = 0;
        if (topic is null or OutboxTopics.Notification) requeued += await notifications.RetryRejectedAsync(topic, ct);
        if (topic is null or OutboxTopics.Audit or OutboxTopics.SystemLog) requeued += await audit.RetryRejectedAsync(topic, ct);
        return Ok(new { requeued });
    }
}
