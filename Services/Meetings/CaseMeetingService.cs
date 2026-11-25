namespace CaseManagement.Api.Services.Meetings;

using System.Globalization;
using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.EmailIntake;
using Microsoft.EntityFrameworkCore;

public sealed class CaseMeetingDto
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public string CaseNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string TimeZone { get; set; } = string.Empty;
    /// <summary>"Thu 26 Nov 2026, 10:00–10:30 (GMT+8)", in the meeting's time zone.</summary>
    public string When { get; set; } = string.Empty;
    public string? Agenda { get; set; }
    public string? JoinUrl { get; set; }
    public string Provider { get; set; } = string.Empty;
    public Guid OrganizerUserId { get; set; }
    public string OrganizerName { get; set; } = string.Empty;
    public string AttendeeEmail { get; set; } = string.Empty;
    public string? AttendeeName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? CancelReason { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>The last invitation email: Pending, Sending, Sent or Failed (and why).</summary>
    public string? EmailStatus { get; set; }
    public string? EmailError { get; set; }
    /// <summary>The caller may move or cancel it (its organizer, the case's holder, or a supervisor), and it is still ahead.</summary>
    public bool CanChange { get; set; }
}

public sealed class SaveCaseMeetingDto
{
    public string? Title { get; set; }
    /// <summary>yyyy-MM-dd, in the platform time zone.</summary>
    public string? Date { get; set; }
    /// <summary>HH:mm, in the platform time zone.</summary>
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public string? Agenda { get; set; }
    public string? JoinUrl { get; set; }
    public string? AttendeeEmail { get; set; }
    public string? AttendeeName { get; set; }
    /// <summary>
    /// The mailbox the invitation goes from, when the case's team works several and the case has none of its own
    /// (<see cref="MeetingOptionsDto.Mailboxes"/>). Ignored otherwise; a change to a meeting keeps the mailbox it was first sent from.
    /// </summary>
    public Guid? MailboxId { get; set; }
}

public sealed class CancelCaseMeetingDto
{
    public string? Reason { get; set; }
}

/// <summary>What the Schedule Meeting form needs: the time zone times are in, who it goes to and from, and whether it can be emailed.</summary>
/// <param name="CustomerEmailSource">Where <paramref name="CustomerEmail"/> came from: "case" (the case's contact address), "email" (the
/// email the case came from), "customer" (the customer record), or null when none is known and the agent types it.</param>
/// <param name="Mailboxes">Set when the invitation's mailbox must be chosen: the case has none of its own and its team works several.
/// The form then asks, and sends the choice as <see cref="SaveCaseMeetingDto.MailboxId"/>.</param>
public sealed record MeetingOptionsDto(string TimeZone, string TimeZoneLabel, string? CustomerEmail, string? CustomerName, string OrganizerName,
    bool CanEmail, string? MailboxAddress, string? Reason, string? CustomerEmailSource = null, string? SourceChannel = null,
    IReadOnlyList<MailboxOptionDto>? Mailboxes = null);

public interface ICaseMeetingService
{
    Task<IReadOnlyList<CaseMeetingDto>> ListAsync(Guid caseId, CancellationToken ct = default);
    Task<MeetingOptionsDto> OptionsAsync(Guid caseId, CancellationToken ct = default);
    Task<CaseMeetingDto> ScheduleAsync(Guid caseId, SaveCaseMeetingDto dto, CancellationToken ct = default);
    Task<CaseMeetingDto> RescheduleAsync(Guid caseId, Guid meetingId, SaveCaseMeetingDto dto, CancellationToken ct = default);
    Task<CaseMeetingDto> CancelAsync(Guid caseId, Guid meetingId, CancelCaseMeetingDto dto, CancellationToken ct = default);
}

/// <summary>
/// Schedule Meeting on a case: the meeting is saved against the case, the customer is emailed its details with a calendar invite
/// (through the case's own mailbox and outbox — retried, and shown as failed on the timeline if it cannot go), and it appears on
/// the timeline and the Calendar. Moving or cancelling it sends an update or cancellation of the same calendar entry.
/// </summary>
/// <remarks>
/// <para><b>Times are the platform's.</b> The form gives a date and times in the Host's time zone; they are stored in UTC and
/// every email and screen shows them back in that zone, with its offset.</para>
/// <para><b>The link is pasted.</b> Automatic links (Microsoft Teams, Google Meet) need the organisation's credentials and come
/// later; until then the agent pastes the link of whatever service they use, and it must be a web address.</para>
/// </remarks>
public sealed class CaseMeetingService(AppDbContext db, ISlaClockProvider clocks, ICurrentUserAccessor currentUser) : ICaseMeetingService
{
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(8);
    private static readonly TimeSpan MaxAhead = TimeSpan.FromDays(366);

    private Guid Me => currentUser.LocalUserId ?? throw new UnauthorizedAccessException("Sign in to schedule a meeting.");

    public async Task<IReadOnlyList<CaseMeetingDto>> ListAsync(Guid caseId, CancellationToken ct = default)
    {
        var zone = (await clocks.GetAsync(ct)).Calendar.TimeZone;
        var c = await db.Cases.AsNoTracking().Where(x => x.Id == caseId).Select(x => new { x.OwnerId, x.CaseNumber }).FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("Case not found.");
        var meetings = await db.CaseMeetings.AsNoTracking().Include(m => m.Organizer).Where(m => m.CaseId == caseId)
            .OrderByDescending(m => m.StartUtc).ToListAsync(ct);
        var emails = await LatestEmailsAsync(meetings.Select(m => m.Id).ToList(), ct);
        return meetings.Select(m => ToDto(m, c.CaseNumber, c.OwnerId, zone, emails.GetValueOrDefault(m.Id))).ToList();
    }

    public async Task<MeetingOptionsDto> OptionsAsync(Guid caseId, CancellationToken ct = default)
    {
        var zone = (await clocks.GetAsync(ct)).Calendar.TimeZone;
        var c = await db.Cases.AsNoTracking().Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == caseId, ct) ?? throw new KeyNotFoundException("Case not found.");
        var me = await db.Users.AsNoTracking().Where(u => u.Id == Me).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "You";
        var (email, source) = await CustomerEmailAsync(c, ct);
        string? mailbox = null, reason = null;
        IReadOnlyList<MailboxOptionDto>? choices = null;
        try { mailbox = (await MailboxForAsync(c, email ?? "customer@example.invalid", null, null, ct)).Address; }
        catch (FieldValidationException ex) { reason = ex.Errors.FirstOrDefault()?.Message; }
        // Several mailboxes and none of the case's own: not a dead end — the person chooses which one the invitation goes from.
        if (mailbox == null && c.ReplyMailboxId == null && await TeamMailboxesAsync(c, ct) is { Count: > 1 } shared)
        {
            choices = shared.Select(m => new MailboxOptionDto(m.Id, m.Name, m.Address)).ToList();
            reason = null;
        }
        return new MeetingOptionsDto(zone.Id, ZoneLabel(zone, DateTime.UtcNow), email, c.Customer?.FullName, me, mailbox != null || choices != null, mailbox, reason,
            source, c.SourceChannel, choices);
    }

    public async Task<CaseMeetingDto> ScheduleAsync(Guid caseId, SaveCaseMeetingDto dto, CancellationToken ct = default)
    {
        var c = await LoadCaseAsync(caseId, ct);
        var zone = (await clocks.GetAsync(ct)).Calendar.TimeZone;
        var (title, start, end, url, attendee) = Validate(dto, zone);

        // The same meeting twice (a double click, a second tab): refused rather than sent twice.
        if (await db.CaseMeetings.AnyAsync(m => m.CaseId == caseId && m.Status == CaseMeetingStatuses.Scheduled && m.StartUtc == start && m.AttendeeEmail == attendee, ct))
            throw new FieldValidationException([new FieldError("startTime", "A meeting with this customer at this time is already scheduled on this case.")]);

        var mailbox = await MailboxForAsync(c, attendee, dto.MailboxId, null, ct);
        var meeting = new CaseMeeting
        {
            CaseId = caseId, Title = title, StartUtc = start, EndUtc = end, TimeZone = zone.Id, Agenda = Clean(dto.Agenda, 4000), JoinUrl = url,
            OrganizerUserId = Me, AttendeeEmail = attendee, AttendeeName = Clean(dto.AttendeeName, 200),
            IcsUid = $"{Guid.NewGuid():N}@casemanagement.omniconnect", Sequence = 0, CreatedAt = DateTime.UtcNow,
        };
        db.CaseMeetings.Add(meeting);
        await RecordAsync(c, meeting, mailbox, "MEETING_SCHEDULED", "REQUEST", zone,
            $"Meeting scheduled: {title}, {When(meeting, zone)}, with {attendee}.", ct);
        return (await ListAsync(caseId, ct)).First(m => m.Id == meeting.Id);
    }

    public async Task<CaseMeetingDto> RescheduleAsync(Guid caseId, Guid meetingId, SaveCaseMeetingDto dto, CancellationToken ct = default)
    {
        var c = await LoadCaseAsync(caseId, ct);
        var meeting = await ChangeableAsync(c, meetingId, ct);
        var zone = (await clocks.GetAsync(ct)).Calendar.TimeZone;
        var (title, start, end, url, attendee) = Validate(dto, zone);
        var mailbox = await MailboxForAsync(c, attendee, dto.MailboxId, meeting.Id, ct);
        var before = When(meeting, zone);

        meeting.Title = title; meeting.StartUtc = start; meeting.EndUtc = end; meeting.JoinUrl = url; meeting.TimeZone = zone.Id;
        meeting.Agenda = Clean(dto.Agenda, 4000); meeting.AttendeeEmail = attendee; meeting.AttendeeName = Clean(dto.AttendeeName, 200);
        meeting.Sequence++; meeting.UpdatedAt = DateTime.UtcNow; meeting.Version++;
        await RecordAsync(c, meeting, mailbox, "MEETING_RESCHEDULED", "REQUEST", zone,
            $"Meeting updated: {title}, now {When(meeting, zone)} (was {before}).", ct);
        return (await ListAsync(caseId, ct)).First(m => m.Id == meeting.Id);
    }

    public async Task<CaseMeetingDto> CancelAsync(Guid caseId, Guid meetingId, CancelCaseMeetingDto dto, CancellationToken ct = default)
    {
        var c = await LoadCaseAsync(caseId, ct, allowFinished: true);
        var meeting = await ChangeableAsync(c, meetingId, ct);
        var zone = (await clocks.GetAsync(ct)).Calendar.TimeZone;
        var mailbox = await MailboxForAsync(c, meeting.AttendeeEmail, null, meeting.Id, ct);
        meeting.Status = CaseMeetingStatuses.Cancelled;
        meeting.CancelReason = Clean(dto.Reason, 500);
        meeting.Sequence++; meeting.UpdatedAt = DateTime.UtcNow; meeting.Version++;
        await RecordAsync(c, meeting, mailbox, "MEETING_CANCELLED", "CANCEL", zone,
            $"Meeting cancelled: {meeting.Title}, {When(meeting, zone)}.{(meeting.CancelReason != null ? " Reason: " + meeting.CancelReason : "")}", ct);
        return (await ListAsync(caseId, ct)).First(m => m.Id == meeting.Id);
    }

    /// <summary>
    /// The customer's address the invitation should go to, from what is already known — so the agent never retypes it: the
    /// case's contact address (set for email cases and replies), else the address of the email the case came from, else the
    /// customer record's. Only a well-formed address is offered; otherwise the field starts empty for the agent to fill in.
    /// </summary>
    private async Task<(string? Email, string? Source)> CustomerEmailAsync(Case c, CancellationToken ct)
    {
        static string? Valid(string? e)
        {
            var v = e?.Trim().ToLowerInvariant();
            return !string.IsNullOrEmpty(v) && v.Length <= 254 && System.Net.Mail.MailAddress.TryCreate(v, out _) ? v : null;
        }
        if (Valid(c.ContactEmail) is { } contact) return (contact, "case");
        var fromEmail = await db.InboundEmails.AsNoTracking().Where(e => e.CaseId == c.Id).OrderBy(e => e.ReceivedAt)
            .Select(e => e.FromAddress).FirstOrDefaultAsync(ct);
        if (Valid(fromEmail) is { } sender) return (sender, "email");
        if (Valid(c.Customer?.Email) is { } customer) return (customer, "customer");
        return (null, null);
    }

    // ------------------------------------------------------------------------------------------------ rules

    private async Task<Case> LoadCaseAsync(Guid caseId, CancellationToken ct, bool allowFinished = false)
    {
        var c = await db.Cases.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == caseId, ct) ?? throw new KeyNotFoundException("Case not found.");
        if (!allowFinished && c.Status is CaseStatus.Resolved or CaseStatus.Closed or CaseStatus.Cancelled)
            throw new InvalidOperationException($"{c.CaseNumber} is resolved. Reopen it to schedule a meeting.");
        return c;
    }

    private async Task<CaseMeeting> ChangeableAsync(Case c, Guid meetingId, CancellationToken ct)
    {
        var meeting = await db.CaseMeetings.FirstOrDefaultAsync(m => m.Id == meetingId && m.CaseId == c.Id, ct) ?? throw new KeyNotFoundException("Meeting not found.");
        if (meeting.Status == CaseMeetingStatuses.Cancelled) throw new InvalidOperationException("This meeting was already cancelled.");
        if (meeting.EndUtc < DateTime.UtcNow) throw new InvalidOperationException("This meeting has already taken place.");
        if (!MayChange(meeting.OrganizerUserId, c.OwnerId))
            throw new CaseLockedException("Only the meeting's organizer, the case's holder or a supervisor can change this meeting.");
        return meeting;
    }

    private bool MayChange(Guid organizerId, Guid ownerId) =>
        currentUser.LocalUserId is { } me && (me == organizerId || me == ownerId || currentUser.Has(Permissions.CasesSupervise));

    private static (string Title, DateTime Start, DateTime End, string? Url, string Attendee) Validate(SaveCaseMeetingDto dto, TimeZoneInfo zone)
    {
        var errors = new List<FieldError>();
        var title = (dto.Title ?? "").Trim();
        if (title.Length == 0) errors.Add(new FieldError("title", "Give the meeting a title."));
        else if (title.Length > 200) errors.Add(new FieldError("title", "Keep the title to 200 characters."));

        DateTime start = default, end = default;
        if (!DateOnly.TryParseExact(dto.Date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            errors.Add(new FieldError("date", "Choose the meeting date."));
        else if (!TimeOnly.TryParseExact(dto.StartTime ?? "", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
            errors.Add(new FieldError("startTime", "Choose the start time."));
        else if (!TimeOnly.TryParseExact(dto.EndTime ?? "", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
            errors.Add(new FieldError("endTime", "Choose the end time."));
        else
        {
            start = ToUtc(day.ToDateTime(from), zone);
            end = ToUtc(day.ToDateTime(to), zone);
            var now = DateTime.UtcNow;
            if (end <= start) errors.Add(new FieldError("endTime", "The meeting must end after it starts."));
            else if (end - start > MaxDuration) errors.Add(new FieldError("endTime", "A meeting can last at most 8 hours."));
            if (start < now.AddMinutes(-1)) errors.Add(new FieldError("startTime", "That time has already passed. Choose a time ahead."));
            else if (start > now + MaxAhead) errors.Add(new FieldError("date", "A meeting can be booked up to a year ahead."));
        }

        string? url = null;
        var rawUrl = (dto.JoinUrl ?? "").Trim();
        if (rawUrl.Length > 0)
        {
            if (rawUrl.Length > 1000 || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http"))
                errors.Add(new FieldError("joinUrl", "Paste the meeting's web link (starting with https://)."));
            else url = parsed.ToString();
        }

        var attendee = (dto.AttendeeEmail ?? "").Trim().ToLowerInvariant();
        if (attendee.Length == 0) errors.Add(new FieldError("attendeeEmail", "Enter the customer's email address: the invitation goes there."));
        else if (attendee.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(attendee, out _))
            errors.Add(new FieldError("attendeeEmail", "Enter a valid email address."));

        if (errors.Count > 0) throw new FieldValidationException(errors);
        return (title, start, end, url, attendee);
    }

    /// <summary>A wall-clock time in the zone as UTC; a time skipped by a daylight-saving change is refused.</summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified)) throw new FieldValidationException([new FieldError("startTime", "That time does not exist in this time zone (a clock change). Choose another.")]);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    /// <summary>
    /// The mailbox an invitation goes from. A change to a meeting (update, cancellation) goes from the mailbox its invitation was
    /// first sent from, so the customer's calendar sees the same organizer; a new one from the case's own mailbox, its team's only
    /// one, or — the team working several — the one chosen (<paramref name="chosenMailboxId"/>).
    /// </summary>
    private async Task<EmailMailbox> MailboxForAsync(Case c, string attendee, Guid? chosenMailboxId, Guid? meetingId, CancellationToken ct)
    {
        if (meetingId is { } id)
        {
            var first = await db.CaseEmailMessages.AsNoTracking()
                .Where(m => m.CaseEventId != null && db.CaseEvents.Any(e => e.Id == m.CaseEventId && e.MeetingId == id))
                .OrderBy(m => m.CreatedAt).Select(m => (Guid?)m.MailboxId).FirstOrDefaultAsync(ct);
            if (first is { } used && await db.EmailMailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == used, ct) is { IsEnabled: true, PasswordEncrypted: not null } box)
                return box;
        }
        return await CaseEmailOutbox.ResolveMailboxAsync(db, new Case { Id = c.Id, TeamId = c.TeamId, ReplyMailboxId = c.ReplyMailboxId, ContactEmail = attendee },
            chosenMailboxId, ct);
    }

    /// <summary>The usable mailboxes the case's team works (what a person may choose an invitation's mailbox from).</summary>
    private Task<List<EmailMailbox>> TeamMailboxesAsync(Case c, CancellationToken ct) =>
        db.EmailMailboxes.AsNoTracking()
            .Where(m => m.IsEnabled && m.PasswordEncrypted != null && m.Teams.Any(t => t.TeamId == c.TeamId)).OrderBy(m => m.Name).ToListAsync(ct);

    /// <summary>The timeline entry, the email with its calendar invite (sent by the outbox), saved together.</summary>
    private async Task RecordAsync(Case c, CaseMeeting meeting, EmailMailbox mailbox, string action, string method, TimeZoneInfo zone, string summary, CancellationToken ct)
    {
        var organizer = await db.Users.AsNoTracking().Where(u => u.Id == meeting.OrganizerUserId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "Case Management";
        var caseEvent = new CaseEvent
        {
            Id = Guid.NewGuid(), CaseId = c.Id, EventType = EventType.Note, ActionType = action, Channel = "Email", IsInternal = false,
            CreatedAt = DateTime.UtcNow, UserId = Me, MeetingId = meeting.Id, Message = summary, Module = "Meetings",
        };
        db.CaseEvents.Add(caseEvent);

        var subject = method == "CANCEL" ? $"Cancelled: {meeting.Title} [Case {c.CaseNumber}]"
            : action == "MEETING_RESCHEDULED" ? $"Updated: {meeting.Title} [Case {c.CaseNumber}]" : $"Meeting: {meeting.Title} [Case {c.CaseNumber}]";
        var domain = EmailParsing.DomainOf(mailbox.Address) is { Length: > 0 } d ? d : "casemanagement.local";
        db.CaseEmailMessages.Add(new CaseEmailMessage
        {
            CaseId = c.Id, CaseEventId = caseEvent.Id, MailboxId = mailbox.Id, ToAddress = meeting.AttendeeEmail,
            Subject = EmailParsing.Truncate(subject, 500), BodyText = EmailBody(meeting, c, organizer, zone, method, action),
            MessageId = EmailParsing.NormalizeMessageId(MimeKit.Utils.MimeUtils.GenerateMessageId(domain))!,
            Status = CaseEmailStatuses.Pending, NextAttemptAt = DateTime.UtcNow, SentById = Me,
            CalendarIcs = MeetingInvite.Build(meeting, c, organizer, mailbox.Address, method, DateTime.UtcNow), CalendarMethod = method,
        });
        await db.SaveChangesAsync(ct);
    }

    private static string EmailBody(CaseMeeting m, Case c, string organizer, TimeZoneInfo zone, string method, string action)
    {
        var greeting = string.IsNullOrWhiteSpace(m.AttendeeName) ? "Dear customer," : $"Dear {m.AttendeeName},";
        var lead = method == "CANCEL" ? "The following meeting about your case has been cancelled."
            : action == "MEETING_RESCHEDULED" ? "The meeting about your case has been changed. The new details are below."
            : "A meeting about your case has been scheduled. The details are below; the attached invitation adds it to your calendar.";
        var lines = new List<string> { greeting, "", lead, "", $"Meeting: {m.Title}", $"When: {When(m, zone)}", $"Time zone: {zone.Id}" };
        if (method != "CANCEL" && !string.IsNullOrWhiteSpace(m.JoinUrl)) lines.Add($"Join: {m.JoinUrl}");
        lines.Add($"Case reference: {c.CaseNumber}");
        lines.Add($"Organizer: {organizer}");
        if (method == "CANCEL" && !string.IsNullOrWhiteSpace(m.CancelReason)) lines.Add($"Reason: {m.CancelReason}");
        if (method != "CANCEL" && !string.IsNullOrWhiteSpace(m.Agenda)) { lines.Add(""); lines.Add("Agenda:"); lines.Add(m.Agenda.Trim()); }
        lines.Add("");
        lines.Add("Kind regards,");
        lines.Add(organizer);
        return string.Join("\n", lines);
    }

    private async Task<Dictionary<Guid, CaseEmailMessage>> LatestEmailsAsync(List<Guid> meetingIds, CancellationToken ct)
    {
        if (meetingIds.Count == 0) return new();
        var rows = await db.CaseEvents.AsNoTracking().Where(e => e.MeetingId != null && meetingIds.Contains(e.MeetingId.Value) && e.EmailMessage != null)
            .Select(e => new { MeetingId = e.MeetingId!.Value, e.EmailMessage }).ToListAsync(ct);
        return rows.GroupBy(r => r.MeetingId).ToDictionary(g => g.Key, g => g.Select(r => r.EmailMessage!).OrderByDescending(m => m.CreatedAt).First());
    }

    private CaseMeetingDto ToDto(CaseMeeting m, string caseNumber, Guid ownerId, TimeZoneInfo zone, CaseEmailMessage? email) => new()
    {
        Id = m.Id, CaseId = m.CaseId, CaseNumber = caseNumber, Title = m.Title, StartUtc = DateTime.SpecifyKind(m.StartUtc, DateTimeKind.Utc),
        EndUtc = DateTime.SpecifyKind(m.EndUtc, DateTimeKind.Utc), TimeZone = zone.Id, When = When(m, zone), Agenda = m.Agenda, JoinUrl = m.JoinUrl,
        Provider = m.Provider, OrganizerUserId = m.OrganizerUserId, OrganizerName = m.Organizer?.Name ?? "", AttendeeEmail = m.AttendeeEmail,
        AttendeeName = m.AttendeeName, Status = m.Status, CancelReason = m.CancelReason, CreatedAt = DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc),
        EmailStatus = email?.Status, EmailError = email?.Error,
        CanChange = m.Status == CaseMeetingStatuses.Scheduled && m.EndUtc >= DateTime.UtcNow && MayChange(m.OrganizerUserId, ownerId),
    };

    /// <summary>"Thu 26 Nov 2026, 10:00–10:30 (GMT+8)".</summary>
    public static string When(CaseMeeting m, TimeZoneInfo zone)
    {
        var s = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(m.StartUtc, DateTimeKind.Utc), zone);
        var e = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(m.EndUtc, DateTimeKind.Utc), zone);
        return $"{s.ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}–{e.ToString("HH:mm", CultureInfo.InvariantCulture)} ({ZoneLabel(zone, m.StartUtc)})";
    }

    public static string ZoneLabel(TimeZoneInfo zone, DateTime atUtc)
    {
        var offset = zone.GetUtcOffset(DateTime.SpecifyKind(atUtc, DateTimeKind.Utc));
        return offset == TimeSpan.Zero ? "GMT" : $"GMT{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():h\\:mm}".Replace(":00", "");
    }

    private static string? Clean(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
    }
}
