namespace CaseManagement.Api.HostIntegration;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Which mailboxes' emails and tickets the caller may see, one rule for the Inbox and for Tickets.
/// </summary>
/// <remarks>
/// <para>A mailbox is seen by the leads and ACTIVE members of the teams it is shared with (Email Configuration → Inbound
/// Mailboxes → Teams with access), and by nobody else. A mailbox shared with no team is seen only by the people below: a
/// client's mail is never open to everyone just because nobody chose its teams yet (the mailbox list warns about it).</para>
/// <para><see cref="Permissions.TicketsViewAll"/>, <see cref="Permissions.EmailManage"/> (the people who configure the
/// mailboxes) and work with no caller see every mailbox. Like <see cref="CaseAccessScope"/>, it is one SQL predicate over the
/// live tables, so removing someone from a team takes effect on their next request.</para>
/// </remarks>
public interface IMailboxAccessScope
{
    bool SeesAll { get; }

    /// <summary>The ids of the mailboxes the caller may see, as a query to compose into another.</summary>
    IQueryable<Guid> VisibleMailboxIds();

    Task<bool> CanSeeMailboxAsync(Guid mailboxId, CancellationToken ct = default);
}

public sealed class MailboxAccessScope(AppDbContext db, ICurrentUserAccessor? currentUser = null) : IMailboxAccessScope
{
    public bool SeesAll => currentUser?.LocalUserId == null || currentUser.Has(Permissions.TicketsViewAll) || currentUser.Has(Permissions.EmailManage);

    public IQueryable<Guid> VisibleMailboxIds()
    {
        if (SeesAll) return db.EmailMailboxes.Select(m => m.Id);
        var me = currentUser!.LocalUserId!.Value;
        return db.EmailMailboxes
            .Where(m => m.Teams.Any(t => t.Team!.OwnerId == me || db.TeamMembers.Any(tm => tm.TeamId == t.TeamId && tm.UserId == me && tm.IsActive)))
            .Select(m => m.Id);
    }

    public async Task<bool> CanSeeMailboxAsync(Guid mailboxId, CancellationToken ct = default) =>
        SeesAll || await VisibleMailboxIds().AnyAsync(id => id == mailboxId, ct);
}

/// <summary>An email or ticket of a mailbox the caller may not see. Answered 404, so its existence is not revealed.</summary>
public sealed class MailboxAccessDeniedException() : KeyNotFoundException("Not found.");
