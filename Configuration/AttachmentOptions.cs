namespace CaseManagement.Api.Configuration;

/// <summary>
/// Attachment upload constraints and storage, configurable per-environment.
/// </summary>
public class AttachmentOptions
{
    public const string SectionName = "Attachments";

    /// <summary>Maximum file size in bytes. Default: 25 MB.</summary>
    public long MaxFileSizeBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>
    /// Comma-separated list of allowed file extensions (lower-case, with leading dot).
    /// Every allowed extension must be one the content check knows how to verify (see AttachmentValidator.SupportedExtensions);
    /// an extension outside that set is never accepted, even if listed. SVG is deliberately absent: it can carry script.
    /// </summary>
    public string AllowedExtensions { get; set; } =
        ".pdf,.doc,.docx,.xls,.xlsx,.csv,.txt,.png,.jpg,.jpeg,.gif,.bmp,.zip,.rar,.7z,.msg,.eml";

    /// <summary>
    /// Where uploaded files are kept on disk. Empty = "Uploads/Attachments" under the application folder.
    /// IMPORTANT in production: point this at a PERSISTENT volume. On hosts with an ephemeral file system (e.g. a free Render
    /// instance) files under the application folder disappear on every deploy or restart.
    /// </summary>
    public string? StoragePath { get; set; }

    /// <summary>
    /// Where new files go: "Database" (default — every server sharing the database can read them) or "LocalDisk" (the folder
    /// above only; a file received by one server cannot be opened on another). Reads always also look on the local disk, so
    /// files saved there before the switch keep opening.
    /// </summary>
    public string Store { get; set; } = "Database";

    /// <summary>
    /// With the Database store: on startup, copy any file still only on this server's disk into the database (once, idempotent),
    /// so files received by this server before the switch open everywhere.
    /// </summary>
    public bool ImportLocalFilesOnStartup { get; set; } = true;

    public HashSet<string> GetAllowedExtensionSet()
    {
        if (string.IsNullOrWhiteSpace(AllowedExtensions)) return new HashSet<string>();
        return AllowedExtensions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .ToHashSet();
    }
}
