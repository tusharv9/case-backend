namespace CaseManagement.Api.Services;

/// <summary>
/// Raises Case Management notifications. They are delivered by OmniConnect's central notification system (AuthService):
/// shown in the Host's one bell, live, and configurable per kind on Settings → Manage Notifications. Nothing is stored here.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Reports one event about a case to one person (a Case Management user). <paramref name="type"/> is the event's name in
    /// this code (CASE_ASSIGNED, USER_MENTIONED…), mapped to its declared kind. <paramref name="title"/> and
    /// <paramref name="message"/> are not sent: the card is worded by the kind's template from the case's number, type and
    /// severity, never its title or customer — except a nudge, whose message is the supervisor's own words.
    /// An event with an <paramref name="eventKey"/> is shown once, however often it is reported. Never throws.
    /// </summary>
    Task CreateNotificationAsync(Guid recipientUserId, string type, string title, string message, Guid? caseId = null, string? caseNumber = null, string priority = "High", int reminderCount = 0, CancellationToken ct = default, string? eventKey = null);

    /// <summary>
    /// Tells owners about cases the SLA clock reads as breached: once when the breach is first seen (one combined notification
    /// when several of one owner's cases breach together), then reminders after a cooldown, up to a maximum. Called by the SLA
    /// monitor with the verdicts it computed — never as a side effect of a read.
    /// </summary>
    Task PublishSlaBreachNotificationsAsync(IReadOnlyList<SlaBreachCandidate> breached, DateTime now, CancellationToken ct = default);

    /// <summary>Tells the people who manage Case Settings that configuration changed. <paramref name="message"/> is the detail line.</summary>
    Task CreateConfigChangedNotificationAsync(string title, string message, CancellationToken ct = default);
}

public sealed record SlaBreachCandidate(Guid Id, string CaseNumber, string Title, Guid OwnerId, DateTime SlaStartTime);
