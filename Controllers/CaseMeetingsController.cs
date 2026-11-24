using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.EmailIntake;
using CaseManagement.Api.Services.Meetings;
using Microsoft.AspNetCore.Mvc;

namespace CaseManagement.Api.Controllers;

/// <summary>
/// Meetings with the customer on a case (Workflow Timeline → Schedule Meeting). Reading needs the case; scheduling, moving and
/// cancelling need Work cases, and — like any other work — the case must have been started (Handle) by its holder.
/// </summary>
[ApiController]
[Route("api/cases/{id:guid}/meetings")]
[RequirePermission(Permissions.CasesRead, Permissions.CasesWrite)]
[CaseAccess("id")]
public class CaseMeetingsController(ICaseMeetingService meetings, ICaseService cases, CaseEmailSender emails) : BaseApiController
{
    [HttpGet]
    public async Task<IActionResult> List(Guid id, CancellationToken ct) => Ok(await meetings.ListAsync(id, ct));

    /// <summary>What the form needs: the time zone, the customer's address, and whether the invitation can be emailed.</summary>
    [HttpGet("options")]
    public async Task<IActionResult> Options(Guid id, CancellationToken ct) => Ok(await meetings.OptionsAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Schedule(Guid id, [FromBody] SaveCaseMeetingDto dto, CancellationToken ct)
    {
        await cases.EnsureWorkMayStartAsync(id, CurrentUserId);
        var meeting = await meetings.ScheduleAsync(id, dto, ct);
        return Ok(await SendAsync(id, meeting.Id, ct));
    }

    [HttpPut("{meetingId:guid}")]
    public async Task<IActionResult> Reschedule(Guid id, Guid meetingId, [FromBody] SaveCaseMeetingDto dto, CancellationToken ct)
    {
        await cases.EnsureWorkMayStartAsync(id, CurrentUserId);
        await meetings.RescheduleAsync(id, meetingId, dto, ct);
        return Ok(await SendAsync(id, meetingId, ct));
    }

    [HttpPost("{meetingId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, Guid meetingId, [FromBody] CancelCaseMeetingDto dto, CancellationToken ct)
    {
        await meetings.CancelAsync(id, meetingId, dto, ct);
        return Ok(await SendAsync(id, meetingId, ct));
    }

    /// <summary>The invitation is sent now; a failure stays on the email (shown on the meeting and the timeline) and is retried.</summary>
    private async Task<CaseMeetingDto> SendAsync(Guid caseId, Guid meetingId, CancellationToken ct)
    {
        await emails.SendForCaseAsync(caseId, ct);
        return (await meetings.ListAsync(caseId, ct)).First(m => m.Id == meetingId);
    }
}
