namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;

public interface INotificationService
{
    Task CreateNotificationAsync(Guid recipientUserId, string type, string title, string message, Guid? caseId = null, string? caseNumber = null, string priority = "High", int reminderCount = 0, CancellationToken ct = default, string? eventKey = null);
    Task<PagedResponseDto<NotificationDto>> GetNotificationsAsync(Guid userId, int page = 1, int pageSize = 15, CancellationToken ct = default);
    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken ct = default);
    Task MarkAsReadAsync(Guid notificationId, Guid userId, CancellationToken ct = default);
    Task MarkAllAsReadAsync(Guid userId, CancellationToken ct = default);
    Task DeleteNotificationAsync(Guid notificationId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Notifies owners of cases the SLA clock reads as breached: once when the breach is first seen, then reminders after a
    /// cooldown, up to a maximum. Called by the SLA monitor with the verdicts it computed — never as a side effect of a read.
    /// </summary>
    Task PublishSlaBreachNotificationsAsync(IReadOnlyList<SlaBreachCandidate> breached, DateTime now, CancellationToken ct = default);

    /// <summary>Deletes notifications past their retention (read ones sooner than unread). Returns how many were removed.</summary>
    Task<int> PurgeExpiredAsync(DateTime nowUtc, CancellationToken ct = default);

    Task CreateConfigChangedNotificationAsync(string title, string message, CancellationToken ct = default);
}

public sealed record SlaBreachCandidate(Guid Id, string CaseNumber, string Title, Guid OwnerId, DateTime SlaStartTime);
