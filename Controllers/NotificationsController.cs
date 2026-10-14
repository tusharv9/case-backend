namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class NotificationsController : BaseApiController
{
    private readonly INotificationService _notificationService;

    public NotificationsController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    [HttpGet]
    public async Task<IActionResult> GetNotifications([FromQuery] int page = 1, [FromQuery] int pageSize = 15, CancellationToken ct = default)
    {
        var result = await _notificationService.GetNotificationsAsync(CurrentUserId, page, pageSize, ct);
        return Ok(result);
    }

    [HttpGet("unread-count")]
    public async Task<IActionResult> GetUnreadCount(CancellationToken ct = default)
    {
        var unreadCount = await _notificationService.GetUnreadCountAsync(CurrentUserId, ct);
        return Ok(new { unreadCount });
    }

    [HttpPut("{id:guid}/read")]
    public async Task<IActionResult> MarkAsRead(Guid id, CancellationToken ct = default)
    {
        await _notificationService.MarkAsReadAsync(id, CurrentUserId, ct);
        return Ok(new { message = "Notification marked as read." });
    }

    [HttpPut("read-all")]
    public async Task<IActionResult> MarkAllAsRead(CancellationToken ct = default)
    {
        await _notificationService.MarkAllAsReadAsync(CurrentUserId, ct);
        return Ok(new { message = "All notifications marked as read." });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteNotification(Guid id, CancellationToken ct = default)
    {
        await _notificationService.DeleteNotificationAsync(id, CurrentUserId, ct);
        return Ok(new { message = "Notification deleted successfully." });
    }
}
