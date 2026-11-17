namespace CaseManagement.Api.Services.EmailIntake;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Notifications;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Tells the right people about an email waiting for review: when it starts waiting and at its reminder (the leads and active
/// members of the mailbox's teams), and when it is escalated (the teams' leads, or the administrators, as the mailbox says).
/// With nobody named, the notification goes to the administrators (its default), so a waiting email is never seen by nobody.
/// </summary>
/// <remarks>The sender's address and the subject are never put in a notification: it names the mailbox and the kind of sender.</remarks>
public static class ReviewAlerts
{
    public enum Kind { Needed, Reminder, Escalated }

    public static async Task NotifyAsync(AppDbContext db, ICaseNotificationSink? sink, Kind kind, InboundEmail email, EmailMailbox mailbox, ILogger logger, CancellationToken ct)
    {
        if (sink == null) return;
        try
        {
            var recipients = kind == Kind.Escalated && mailbox.ReviewEscalateTo == ReviewEscalationTargets.Administrators
                ? new List<Guid>()
                : await RecipientsAsync(db, mailbox.Id, leadsOnly: kind == Kind.Escalated, ct);
            sink.Enqueue(new CaseNotificationRequest(
                kind switch { Kind.Needed => CaseNotificationKinds.EmailReviewNeeded, Kind.Reminder => CaseNotificationKinds.EmailReviewReminder, _ => CaseNotificationKinds.EmailReviewEscalated },
                recipients,
                EventKey: $"email-review:{kind.ToString().ToLowerInvariant()}:{email.Id}",
                Values: new Dictionary<string, string?>
                {
                    ["mailbox"] = mailbox.Name,
                    ["match"] = MatchLabel(email.MatchKind),
                    ["due"] = email.ReviewDueAt?.ToString("dd MMM HH:mm 'UTC'") ?? "no deadline",
                    ["since"] = email.ReviewStartedAt?.ToString("dd MMM HH:mm 'UTC'"),
                },
                Link: $"inbox/{email.Id}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not raise the {Kind} review notification for email {Email}.", kind, email.Id);
        }
    }

    /// <summary>The leads (and, unless <paramref name="leadsOnly"/>, the active members) of the mailbox's active teams.</summary>
    public static async Task<List<Guid>> RecipientsAsync(AppDbContext db, Guid mailboxId, bool leadsOnly, CancellationToken ct)
    {
        var teamIds = await db.EmailMailboxTeams.AsNoTracking().Where(x => x.MailboxId == mailboxId && x.Team!.IsActive).Select(x => x.TeamId).ToListAsync(ct);
        var leads = await db.Teams.AsNoTracking().Where(t => teamIds.Contains(t.Id) && t.OwnerId != null).Select(t => t.OwnerId!.Value).ToListAsync(ct);
        if (leadsOnly) return leads.Distinct().ToList();
        var members = await db.TeamMembers.AsNoTracking().Where(m => teamIds.Contains(m.TeamId) && m.IsActive).Select(m => m.UserId).ToListAsync(ct);
        return leads.Concat(members).Distinct().ToList();
    }

    public static string MatchLabel(string? matchKind) => matchKind switch
    {
        SenderMatchKinds.KnownDomain => "Sender not registered",
        SenderMatchKinds.SimilarDomain => "Looks like a registered organisation",
        SenderMatchKinds.Unknown => "Unknown sender",
        _ => "Sender not registered",
    };
}
