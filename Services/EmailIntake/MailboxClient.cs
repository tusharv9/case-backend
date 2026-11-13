namespace CaseManagement.Api.Services.EmailIntake;

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using CaseManagement.Api.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

/// <summary>How to reach one mailbox: the stored settings plus the decrypted password (held only for the call).</summary>
public sealed record MailboxConnection(
    string Address, string Username, string Password,
    string ImapHost, int ImapPort, string ImapSecurity,
    string SmtpHost, int SmtpPort, string SmtpSecurity);

/// <summary>Where reading a mailbox got to: the folder's UIDVALIDITY and the highest UID read.</summary>
public sealed record MailboxCursor(long UidValidity, long LastUid);

/// <summary>One new email: the parsed message, or — when it is too large to read — only its envelope.</summary>
public sealed record IncomingMail(long Uid, MimeMessage? Message, long SizeBytes, Envelope? Envelope);

public sealed record MailboxTestResult(bool ImapOk, string? ImapError, bool SmtpOk, string? SmtpError);

/// <summary>Reads a mailbox (IMAP) and sends from it (SMTP). Behind an interface so the pipeline is tested without a mail server.</summary>
public interface IMailboxClient
{
    /// <summary>
    /// Hands every email newer than <paramref name="cursor"/> to <paramref name="handle"/>, oldest first, at most
    /// <paramref name="max"/>. With no cursor nothing is read: the cursor returned starts from now, so connecting a mailbox never
    /// turns its old mail into cases. When the folder's UIDs were renumbered, mail delivered since <paramref name="since"/> is read
    /// again (already-stored emails are recognised and skipped).
    /// </summary>
    Task<MailboxCursor> ReadNewAsync(MailboxConnection mailbox, MailboxCursor? cursor, DateTime? since, int max, long maxBytes,
        Func<IncomingMail, MailboxCursor, Task> handle, CancellationToken ct);

    Task SendAsync(MailboxConnection mailbox, MimeMessage message, CancellationToken ct);

    Task<MailboxTestResult> TestAsync(MailboxConnection mailbox, CancellationToken ct);
}

public sealed class MailKitMailboxClient(ILogger<MailKitMailboxClient> logger) : IMailboxClient
{
    private const int TimeoutMs = 60_000;

    public async Task<MailboxCursor> ReadNewAsync(MailboxConnection mailbox, MailboxCursor? cursor, DateTime? since, int max, long maxBytes,
        Func<IncomingMail, MailboxCursor, Task> handle, CancellationToken ct)
    {
        using var imap = new ImapClient { Timeout = TimeoutMs };
        Harden(imap, mailbox.ImapHost);
        await imap.ConnectAsync(mailbox.ImapHost, mailbox.ImapPort, Security(mailbox.ImapSecurity), ct);
        await imap.AuthenticateAsync(mailbox.Username, mailbox.Password, ct);
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        var validity = (long)inbox.UidValidity;

        IList<UniqueId> uids;
        long lastUid;
        if (cursor == null)
        {
            // First connection: start from now.
            var newest = inbox.UidNext is { } next && next.Id > 1 ? next.Id - 1
                : (await inbox.SearchAsync(SearchQuery.All, ct)).Select(u => (long)u.Id).DefaultIfEmpty(0).Max();
            await imap.DisconnectAsync(true, ct);
            return new MailboxCursor(validity, newest);
        }
        if (cursor.UidValidity != validity)
        {
            logger.LogWarning("Mailbox {Address}: the folder's UIDs were renumbered; re-reading mail delivered since {Since}.", mailbox.Address, since);
            uids = await inbox.SearchAsync(SearchQuery.DeliveredAfter((since ?? DateTime.UtcNow).AddDays(-1)), ct);
            lastUid = 0;
        }
        else
        {
            lastUid = cursor.LastUid;
            uids = lastUid >= uint.MaxValue - 1 ? []
                : await inbox.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId((uint)lastUid + 1), UniqueId.MaxValue)), ct);
        }

        // A range "n:*" always includes the newest message even when it is older than n, so filter again.
        var batch = uids.Where(u => u.Id > lastUid).OrderBy(u => u.Id).Take(Math.Max(1, max)).ToList();
        var position = new MailboxCursor(validity, lastUid);
        if (batch.Count > 0)
        {
            var summaries = await inbox.FetchAsync(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.Envelope, ct);
            foreach (var summary in summaries.OrderBy(s => s.UniqueId.Id))
            {
                ct.ThrowIfCancellationRequested();
                var size = (long)(summary.Size ?? 0);
                var message = size > maxBytes ? null : await inbox.GetMessageAsync(summary.UniqueId, ct);
                position = new MailboxCursor(validity, Math.Max(position.LastUid, summary.UniqueId.Id));
                await handle(new IncomingMail(summary.UniqueId.Id, message, size, summary.Envelope), position);
            }
        }
        await imap.DisconnectAsync(true, ct);
        return position;
    }

    public async Task SendAsync(MailboxConnection mailbox, MimeMessage message, CancellationToken ct)
    {
        using var smtp = new SmtpClient { Timeout = TimeoutMs };
        Harden(smtp, mailbox.SmtpHost);
        await smtp.ConnectAsync(mailbox.SmtpHost, mailbox.SmtpPort, Security(mailbox.SmtpSecurity), ct);
        await smtp.AuthenticateAsync(mailbox.Username, mailbox.Password, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    public async Task<MailboxTestResult> TestAsync(MailboxConnection mailbox, CancellationToken ct)
    {
        string? imapError = null, smtpError = null;
        try
        {
            using var imap = new ImapClient { Timeout = 30_000 };
            Harden(imap, mailbox.ImapHost);
            await imap.ConnectAsync(mailbox.ImapHost, mailbox.ImapPort, Security(mailbox.ImapSecurity), ct);
            await imap.AuthenticateAsync(mailbox.Username, mailbox.Password, ct);
            await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            await imap.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { imapError = Describe(ex); }
        try
        {
            using var smtp = new SmtpClient { Timeout = 30_000 };
            Harden(smtp, mailbox.SmtpHost);
            await smtp.ConnectAsync(mailbox.SmtpHost, mailbox.SmtpPort, Security(mailbox.SmtpSecurity), ct);
            await smtp.AuthenticateAsync(mailbox.Username, mailbox.Password, ct);
            await smtp.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { smtpError = Describe(ex); }
        return new MailboxTestResult(imapError == null, imapError, smtpError == null, smtpError);
    }

    /// <summary>What went wrong, in words an administrator can act on (never the password).</summary>
    public static string Describe(Exception ex) => ex switch
    {
        AuthenticationException => "The email address or password was not accepted. Gmail and Outlook need an app password when two-step sign-in is on.",
        SslHandshakeException => "A secure connection could not be made. Check the port and the security setting (SSL on 993/465, STARTTLS on 587).",
        System.Net.Sockets.SocketException => "The mail server could not be reached. Check the server name and port.",
        TimeoutException or OperationCanceledException => "The mail server did not answer in time.",
        ImapProtocolException or SmtpProtocolException => "The mail server answered unexpectedly: " + ex.Message,
        _ => ex.Message,
    };

    private static SecureSocketOptions Security(string value) =>
        string.Equals(value, MailSecurity.StartTls, StringComparison.OrdinalIgnoreCase) ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;

    /// <summary>
    /// Strict certificate checking with one narrow allowance, the same as AuthService's invite mail: a certificate whose revocation
    /// status this machine cannot confirm (macOS often cannot) is accepted when it is otherwise valid. Any other problem refuses.
    /// </summary>
    private void Harden(MailService client, string host)
    {
        client.ServerCertificateValidationCallback = (_, _, chain, errors) =>
        {
            var accepted = MailCertificatePolicy.Accept(errors, chain?.ChainStatus.Select(s => s.Status), out var tolerated);
            if (tolerated) logger.LogWarning("Mail server {Host}: certificate revocation status could not be confirmed; the certificate is otherwise valid.", host);
            return accepted;
        };
    }
}

/// <summary>The certificate rule for mail servers (see <see cref="MailKitMailboxClient"/>).</summary>
public static class MailCertificatePolicy
{
    private const X509ChainStatusFlags Unconfirmed = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    public static bool Accept(SslPolicyErrors errors, IEnumerable<X509ChainStatusFlags>? chainStatus, out bool toleratedRevocation)
    {
        toleratedRevocation = false;
        if (errors == SslPolicyErrors.None) return true;
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors) return false;
        var flags = (chainStatus ?? []).Aggregate(X509ChainStatusFlags.NoError, (all, f) => all | f);
        if (flags == X509ChainStatusFlags.NoError || (flags & ~Unconfirmed) != 0) return false;
        toleratedRevocation = true;
        return true;
    }
}
