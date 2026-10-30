namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Runs independent reads side by side, each on its own pooled context. Every query costs one round trip to the database
/// however small it is (about a quarter of a second to the hosted database), so a screen that needs eight small reads waits
/// for the slowest of them instead of the sum of all eight.
/// </summary>
/// <remarks>Without a factory (a unit test that builds a service by hand) the same reads run one after another on the
/// request's context. A read must only use the context it is handed — never the request's context in parallel mode.</remarks>
public static class ParallelReads
{
    public static async Task RunAsync(IDbContextFactory<AppDbContext>? contexts, AppDbContext fallback, CancellationToken ct, params Func<AppDbContext, Task>[] reads)
    {
        if (contexts == null)
        {
            foreach (var read in reads) await read(fallback);
            return;
        }

        await Task.WhenAll(reads.Select(async read =>
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            await read(db);
        }));
    }
}
