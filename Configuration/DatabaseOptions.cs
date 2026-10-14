namespace CaseManagement.Api.Configuration;

using CaseManagement.Api.Data;

/// <summary>
/// How the application prepares its database, bound from the "Database" configuration section.
/// All values are optional; the defaults are chosen per environment (see the Resolve… methods).
/// </summary>
public class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Apply pending EF migrations when the application starts. Defaults to true in Development
    /// and false elsewhere, where migrations are meant to be an explicit deployment step
    /// (<c>dotnet CaseManagement.Api.dll --migrate-only</c>). When false and the schema is out of
    /// date the API stays "not ready" and reports why, instead of running against a stale schema.
    /// </summary>
    public bool? MigrateOnStartup { get; set; }

    /// <summary>
    /// Which seed data to create: None, Bootstrap (required configuration only) or Development
    /// (bootstrap plus sample users/teams/customers). Defaults to Development in Development and
    /// Bootstrap elsewhere.
    /// </summary>
    public SeedMode? Seed { get; set; }

    /// <summary>How long to keep retrying while the database is unreachable (e.g. a scale-to-zero
    /// database waking up) before reporting a failure.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 120;

    public bool ResolveMigrateOnStartup(bool isDevelopment) => MigrateOnStartup ?? isDevelopment;

    public SeedMode ResolveSeedMode(bool isDevelopment) =>
        Seed ?? (isDevelopment ? SeedMode.Development : SeedMode.Bootstrap);
}
