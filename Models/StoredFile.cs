namespace CaseManagement.Api.Models;

/// <summary>
/// The bytes of one stored file (a case attachment, a ticket's file, the original of a received email), kept in the database
/// so that every server sharing the database can read it. Before, files lived on the disk of whichever server received them,
/// and any other server answered "not found" (the attachment 404).
/// </summary>
public class StoredFile
{
    /// <summary>The key the rest of the system keeps (<c>CaseAttachment.StoragePath</c>, <c>InboundEmail.RawKey</c>…).</summary>
    public string Key { get; set; } = string.Empty;
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public long Length { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
