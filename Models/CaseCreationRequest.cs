namespace CaseManagement.Api.Models;

/// <summary>
/// One Create Case submission, by its idempotency key (the form sends a new key each time it is opened). A repeat of the same
/// submission — a double click, a retry after a timeout — finds this row and gets the case already created instead of a second
/// one. Written in the case's own transaction; kept a day.
/// </summary>
public class CaseCreationRequest
{
    public string Key { get; set; } = string.Empty;
    public Guid CaseId { get; set; }
    public Case? Case { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
