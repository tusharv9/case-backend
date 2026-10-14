namespace CaseManagement.Api.Data;

using System.Diagnostics;
using CaseManagement.Api.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Prepares the database: waits for it to be reachable, adopts a legacy database or applies EF
/// migrations, then seeds. It replaces the ~100 lines of ad-hoc DDL and the seeder that used to run
/// inline, before the web server could start, on every boot.
///
/// It runs in the background so the process answers <c>/health</c> immediately; the API reports
/// 503 until <see cref="DatabaseInitializationState"/> is ready. It can also be run as an explicit
/// deployment step: <c>dotnet CaseManagement.Api.dll --migrate-only</c>.
/// </summary>
public sealed class DatabaseInitializer : IHostedService
{
    // Serialises seeding across several application instances starting at once.
    private const long SeedAdvisoryLockKey = 727_001;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DatabaseOptions _options;
    private readonly IHostEnvironment _env;
    private readonly DatabaseInitializationState _state;
    private readonly ILogger<DatabaseInitializer> _logger;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public DatabaseInitializer(
        IServiceScopeFactory scopeFactory,
        IOptions<DatabaseOptions> options,
        IHostEnvironment env,
        DatabaseInitializationState state,
        ILogger<DatabaseInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _env = env;
        _state = state;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = Task.Run(() => RunAsync(forceMigrate: false, _cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_runTask != null)
        {
            try { await _runTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>Runs initialisation to completion. Returns true when the database is ready.</summary>
    public async Task<bool> RunAsync(bool forceMigrate, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        _state.Set(DatabaseInitializationStatus.Initializing, "Connecting to the database.");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var isDevelopment = _env.IsDevelopment();
            var migrate = forceMigrate || _options.ResolveMigrateOnStartup(isDevelopment);
            var seedMode = _options.ResolveSeedMode(isDevelopment);

            // 1. Wait for the database (a scale-to-zero database can take a long time to wake).
            var phase = Stopwatch.StartNew();
            await WaitForDatabaseAsync(db, ct);
            _logger.LogInformation("Database reachable after {Elapsed} ms.", phase.ElapsedMilliseconds);

            // 2. Schema.
            _state.Set(DatabaseInitializationStatus.Initializing, "Checking the database schema.");
            phase.Restart();
            var adoptedLegacy = await LegacySchemaAdopter.IsLegacyDatabaseAsync(db, ct);
            var pending = await LegacySchemaAdopter.GetPendingMigrationsAsync(db, ct);

            if (adoptedLegacy || pending.Count > 0)
            {
                if (!migrate)
                {
                    var msg = adoptedLegacy
                        ? "The database predates EF migrations and must be adopted. Run `dotnet CaseManagement.Api.dll --migrate-only` or set Database:MigrateOnStartup=true."
                        : $"The database has {pending.Count} pending migration(s). Run `dotnet CaseManagement.Api.dll --migrate-only` or set Database:MigrateOnStartup=true.";
                    _logger.LogError("{Message}", msg);
                    _state.Set(DatabaseInitializationStatus.MigrationRequired, msg);
                    return false;
                }

                _state.Set(DatabaseInitializationStatus.Initializing, adoptedLegacy ? "Adopting existing database." : "Applying migrations.");
                if (adoptedLegacy)
                {
                    _logger.LogWarning("Existing database created before EF migrations detected; adopting it (additive upgrade, no data is dropped).");
                    await LegacySchemaAdopter.AdoptAsync(db, ct);
                    // Adoption records only the Baseline; apply any migrations added after it.
                    await db.Database.MigrateAsync(ct);
                }
                else
                {
                    await LegacySchemaAdopter.EnsureHistoryTableAsync(db, ct);
                    await db.Database.MigrateAsync(ct);
                }
                _logger.LogInformation("Schema ready after {Elapsed} ms (adopted legacy: {Adopted}, migrations applied: {Count}).",
                    phase.ElapsedMilliseconds, adoptedLegacy, pending.Count);
            }

            // 3. Seed data (each step runs at most once; see DbSeeder).
            if (seedMode != SeedMode.None || adoptedLegacy)
            {
                _state.Set(DatabaseInitializationStatus.Initializing, "Seeding default data.");
                phase.Restart();
                await SeedUnderLockAsync(db, seedMode, adoptedLegacy, ct);
                _logger.LogInformation("Seeding ({Mode}) finished in {Elapsed} ms.", seedMode, phase.ElapsedMilliseconds);
            }

            // 4. Keep the case-number sequence ahead of every existing case number (seed data, restored
            //    backups, imports). Only ever moves forward, so it is safe on every start.
            await db.Database.ExecuteSqlRawAsync(SchemaExtras.SyncCaseNumberSequenceSql, ct);

            _state.Set(DatabaseInitializationStatus.Ready);
            _logger.LogInformation("Database initialisation complete in {Elapsed} ms.", total.ElapsedMilliseconds);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database initialisation failed.");
            _state.Set(DatabaseInitializationStatus.Failed, "Database initialisation failed. See the server log for details.");
            return false;
        }
    }

    private async Task WaitForDatabaseAsync(AppDbContext db, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, _options.ConnectTimeoutSeconds));
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                if (await db.Database.CanConnectAsync(ct)) return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Database connection attempt {Attempt} failed: {Message}", attempt, ex.Message);
            }

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"The database was not reachable within {_options.ConnectTimeoutSeconds} seconds.");

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, attempt)), ct);
        }
    }

    private async Task SeedUnderLockAsync(AppDbContext db, SeedMode mode, bool adoptedLegacy, CancellationToken ct)
    {
        // Session-level advisory lock on a dedicated, explicitly opened connection.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({SeedAdvisoryLockKey})", ct);
            try
            {
                var report = DbSeeder.Run(db, mode, adoptedLegacy);
                foreach (var line in report) _logger.LogInformation("Seed: {Step}", line);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({SeedAdvisoryLockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
