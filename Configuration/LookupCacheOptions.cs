namespace CaseManagement.Api.Configuration;

/// <summary>
/// Expiry windows for cached lookup data, bound from the "LookupCache" configuration section.
/// These are upper bounds on staleness only — the write paths invalidate their own cache entries
/// straight away, so an administrator's change is visible on the next read.
/// </summary>
public class LookupCacheOptions
{
    public const string SectionName = "LookupCache";

    /// <summary>
    /// How long the department list may be served from memory. Set to zero to disable caching.
    /// </summary>
    public int DepartmentsCacheSeconds { get; set; } = 300;
}
