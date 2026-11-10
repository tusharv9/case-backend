namespace CaseManagement.Api.Data;

using Npgsql;

/// <summary>
/// Opens the connection pool's minimum number of connections once the database is ready, then hands them back to the pool.
/// </summary>
/// <remarks>
/// Npgsql never opens connections ahead of time: <c>MinPoolSize</c> only stops idle ones from being closed. A new connection
/// to the hosted database costs a TLS handshake of several round trips (over a second from far away), so after every restart
/// the first pages that read several things side by side — the Dashboard reads about twenty — each waited for connections to
/// be opened. Failures here are logged and ignored: the pool simply grows on demand, as before.
/// </remarks>
public sealed class DatabasePoolWarmup(string connectionString, DatabaseInitializationState dbState, ILogger<DatabasePoolWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await dbState.WhenReady.WaitAsync(stoppingToken);
            var size = new NpgsqlConnectionStringBuilder(connectionString).MinPoolSize;
            if (size <= 0) return;

            var started = DateTime.UtcNow;
            var opened = await Task.WhenAll(Enumerable.Range(0, size).Select(async _ =>
            {
                try
                {
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync(stoppingToken);
                    return true;   // disposing returns it to the pool, still open
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "A warm-up connection could not be opened.");
                    return false;
                }
            }));
            logger.LogInformation("Database pool warmed: {Opened} of {Size} connections open in {Ms} ms.",
                opened.Count(o => o), size, (int)(DateTime.UtcNow - started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Database pool warm-up skipped.");
        }
    }
}
