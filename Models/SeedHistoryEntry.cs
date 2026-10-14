namespace CaseManagement.Api.Models;

/// <summary>
/// Records that a one-time seed step has been applied, so seeding never re-creates data an
/// administrator has since deleted or changed. Keys are versioned (e.g. "bootstrap.lookups.v1"):
/// shipping new default data means adding a new key, not editing an old one.
/// </summary>
public class SeedHistoryEntry
{
    public string Key { get; set; } = string.Empty;
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
}
