namespace CaseManagement.Api.Models;

/// <summary>One saved UI preference of one user (e.g. which view the customer directory opens in). Key/value so a new preference needs no schema change.</summary>
public class UserPreference : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public User? User { get; set; }
}
