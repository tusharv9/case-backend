namespace CaseManagement.Api.Services.EmailIntake;

/// <summary>Email intake settings, bound from <c>EmailIntake</c> (<c>EmailIntake__Enabled</c> and so on in .env).</summary>
public class EmailIntakeOptions
{
    public const string SectionName = "EmailIntake";

    /// <summary>Read mailboxes on this instance. Switch off where no mailbox should be read (e.g. a developer machine sharing a database).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often each mailbox is read, in seconds.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>
    /// 32 random bytes, base64, that encrypt mailbox passwords. When empty a key is derived from
    /// <c>CustomerIdentifiers:EncryptionKey</c>, so no new secret is needed; set this to keep the two apart.
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>Emails larger than this are recorded but not read (no case). Default 30 MB.</summary>
    public long MaxMessageBytes { get; set; } = 30 * 1024 * 1024;

    /// <summary>At most this many emails are read from one mailbox per cycle; the rest wait for the next.</summary>
    public int MaxMessagesPerCycle { get; set; } = 50;

    /// <summary>How many times a failed email, or a failed acknowledgement, is retried automatically.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>At most this many acknowledgements are sent to one address per hour, so a misbehaving sender cannot make us flood them.</summary>
    public int MaxAcknowledgementsPerSenderPerHour { get; set; } = 10;

    /// <summary>The longest case title taken from a subject; longer subjects are cut (the email keeps the whole subject).</summary>
    public int MaxTitleLength { get; set; } = 200;

    /// <summary>The longest case description taken from a body; longer bodies are cut with a note (the original email is kept).</summary>
    public int MaxDescriptionLength { get; set; } = 8000;
}
