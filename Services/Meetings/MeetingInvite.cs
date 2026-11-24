namespace CaseManagement.Api.Services.Meetings;

using System.Globalization;
using System.Text;
using CaseManagement.Api.Models;

/// <summary>
/// The calendar invite (iCalendar, RFC 5545) sent with a meeting email. One UID per meeting and a rising SEQUENCE, so an update
/// moves the same entry in the customer's calendar and a cancellation removes it instead of adding a second one.
/// </summary>
public static class MeetingInvite
{
    public static string Build(CaseMeeting m, Case c, string organizerName, string organizerAddress, string method, DateTime nowUtc)
    {
        var description = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(m.Agenda)) description.Append(m.Agenda.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(m.JoinUrl)) description.Append("Join: ").Append(m.JoinUrl).Append('\n');
        description.Append("Case ").Append(c.CaseNumber);

        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "PRODID:-//OmniConnect//Case Management//EN",
            "VERSION:2.0",
            "CALSCALE:GREGORIAN",
            $"METHOD:{method}",
            "BEGIN:VEVENT",
            $"UID:{m.IcsUid}",
            $"SEQUENCE:{m.Sequence}",
            $"DTSTAMP:{Utc(nowUtc)}",
            $"DTSTART:{Utc(m.StartUtc)}",
            $"DTEND:{Utc(m.EndUtc)}",
            $"SUMMARY:{Escape(m.Title)}",
            $"DESCRIPTION:{Escape(description.ToString())}",
            $"ORGANIZER;CN={Escape(organizerName)}:mailto:{organizerAddress}",
            $"ATTENDEE;CN={Escape(m.AttendeeName ?? m.AttendeeEmail)};ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:{m.AttendeeEmail}",
            $"STATUS:{(method == "CANCEL" ? "CANCELLED" : "CONFIRMED")}",
        };
        if (!string.IsNullOrWhiteSpace(m.JoinUrl))
        {
            lines.Add($"LOCATION:{Escape(m.JoinUrl)}");
            lines.Add($"URL:{m.JoinUrl}");
        }
        lines.Add("END:VEVENT");
        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines.Select(Fold)) + "\r\n";
    }

    private static string Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>TEXT escaping: backslash, semicolon, comma and newlines.</summary>
    public static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    /// <summary>Lines longer than 75 octets are folded (CRLF + space), never inside a UTF-8 character.</summary>
    public static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= 75) return line;
        var sb = new StringBuilder();
        var count = 0;
        foreach (var ch in line)
        {
            var size = Encoding.UTF8.GetByteCount(ch.ToString());
            if (count + size > 75) { sb.Append("\r\n "); count = 1; }
            sb.Append(ch);
            count += size;
        }
        return sb.ToString();
    }
}
