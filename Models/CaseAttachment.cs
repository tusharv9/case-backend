namespace CaseManagement.Api.Models;

public class CaseAttachment : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid CaseId { get; set; }
    public Case Case { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSizeBytes { get; set; }
    public long? FileSize { get; set; }

    public string StoragePath { get; set; } = string.Empty;

    /// <summary>SHA-256 of the stored bytes (hex), for integrity checks. Null for files uploaded before it was recorded.</summary>
    public string? ContentHash { get; set; }
    public string? Note { get; set; }

    public Guid UploadedByUserId { get; set; }
    public User UploadedByUser { get; set; } = null!;
}
