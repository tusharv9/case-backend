namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Runs a change to team membership, team leads or the team policies as one transaction that holds a database-wide lock for
/// its duration. The policies ("one team per person", "one team per lead") are checks across ALL teams, so two saves running
/// side by side could each pass the check and together break the rule; holding the same lock makes them take turns.
/// </summary>
/// <remarks>Outside a relational database (the in-memory provider the unit tests use) the work simply runs: there is no
/// concurrency to guard against there.</remarks>
public static class TeamWriteLock
{
    /// <summary>Arbitrary but fixed: the key of the transaction-scoped advisory lock team writes share.</summary>
    private const long LockKey = 0x5445_414D_5345_5453;   // "TEAMSETS"

    public static async Task RunAsync(AppDbContext context, Func<Task> work, CancellationToken ct)
    {
        if (!context.Database.IsRelational())
        {
            await work();
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(ct);
            await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { LockKey }, ct);
            await work();
            await tx.CommitAsync(ct);
        });
    }
}
