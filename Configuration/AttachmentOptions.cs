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

    public HashSet<string> GetAllowedExtensionSet()
    {
        if (string.IsNullOrWhiteSpace(AllowedExtensions)) return new HashSet<string>();
        return AllowedExtensions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .ToHashSet();
    }
}
