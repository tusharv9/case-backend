using CaseManagement.Api.Services.Notifications;

namespace CaseManagement.Tests;

/// <summary>Collects the notifications a test raised, instead of sending them to AuthService.</summary>
public sealed class RecordingNotificationSink : ICaseNotificationSink
{
    public List<CaseNotificationRequest> Requests { get; } = new();
    public void Enqueue(CaseNotificationRequest request) { lock (Requests) Requests.Add(request); }

    /// <summary>Each (kind, recipient) pair raised, one per recipient.</summary>
    public IEnumerable<(string Kind, Guid Recipient)> Sent =>
        Requests.SelectMany(r => r.RecipientLocalIds.Select(id => (r.Kind, id)));
}
