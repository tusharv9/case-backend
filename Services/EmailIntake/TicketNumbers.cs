namespace CaseManagement.Api.Services.EmailIntake;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Gives a new ticket the next number (T-00001, T-00002…) and saves it. The unique index on <see cref="Ticket.Sequence"/>
/// decides a race between two servers: the loser takes the following number and saves again.
/// </summary>
public static class TicketNumbers
{
    public const string Prefix = "T-";

    /// <summary>Adds <paramref name="ticket"/> with its number and saves everything pending on <paramref name="db"/> with it.</summary>
    public static async Task AddAsync(AppDbContext db, Ticket ticket, CancellationToken ct)
    {
        db.Tickets.Add(ticket);
        for (var attempt = 1; ; attempt++)
        {
            var last = await db.Tickets.AsNoTracking().Select(t => (int?)t.Sequence).MaxAsync(ct) ?? 0;
            ticket.Sequence = last + 1;
            ticket.TicketNumber = $"{Prefix}{ticket.Sequence:D5}";
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (attempt < 5 && db.Entry(ticket).State == EntityState.Added)
            {
                // Someone else took that number at the same moment; try the next one.
            }
        }
    }
}
