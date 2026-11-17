namespace CaseManagement.Api.Services.EmailIntake;

using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Tickets;

/// <summary>
/// The sender-review deadline: how long an email from an unregistered sender may wait for a decision, counted in working time
/// (the calendar cases and tickets use) from when it started waiting. The reminder, deadline and escalation times are taken from
/// the mailbox when the email starts waiting and kept on the email, so changing the mailbox's settings later changes only the
/// emails that start waiting after that.
/// </summary>
public static class ReviewSla
{
    public const string Met = "Met";
    public const string Breached = "Breached";

    /// <summary>Starts the clock once (a later "Check again" that leaves the email waiting keeps its original times).</summary>
    /// <returns>True when the clock started now (the email has just started waiting).</returns>
    public static bool Start(InboundEmail email, EmailMailbox mailbox, TicketSlaClock clock, DateTime nowUtc)
    {
        if (email.ReviewSlaMinutes != null) return false;
        var from = email.ReviewStartedAt ??= nowUtc;
        email.ReviewSlaMinutes = Math.Max(0, mailbox.ReviewSlaMinutes);
        if (mailbox.ReviewSlaMinutes <= 0) return true;
        email.ReviewDueAt = clock.DueAt(from, mailbox.ReviewSlaMinutes);
        email.ReviewReminderAt = mailbox.ReviewReminderMinutes > 0 && mailbox.ReviewReminderMinutes < mailbox.ReviewSlaMinutes
            ? clock.DueAt(from, mailbox.ReviewReminderMinutes) : null;
        email.ReviewEscalateAt = clock.DueAt(from, Math.Max(mailbox.ReviewEscalationMinutes, mailbox.ReviewSlaMinutes));
        return true;
    }

    /// <summary>A decision was made (or the email left the queue): records whether it was in time, once.</summary>
    public static void Stop(InboundEmail email, DateTime atUtc)
    {
        if (email.ReviewDueAt is { } due && email.ReviewSlaOutcome == null) email.ReviewSlaOutcome = atUtc <= due ? Met : Breached;
    }
}
