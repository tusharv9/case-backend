namespace CaseManagement.Api.Data;

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Brings a database that was created by the OLD startup bootstrap (EnsureCreated + ad-hoc DDL,
/// no migration history) under EF Core migrations, without recreating or losing anything:
/// the frozen additive upgrade script runs, then the Baseline migration is recorded as applied.
/// </summary>
public static class LegacySchemaAdopter
{
    /// <summary>Id of the Baseline migration, read from the migration itself so it cannot drift.</summary>
    public static string BaselineMigrationId { get; } =
        typeof(Migrations.Baseline).GetCustomAttribute<MigrationAttribute>()!.Id;

    private static Task<bool> TableExistsAsync(AppDbContext db, string table, CancellationToken ct) =>
        db.Database
            .SqlQueryRaw<bool>($"SELECT to_regclass('public.\"{table}\"') IS NOT NULL AS \"Value\"")
            .SingleAsync(ct);

    /// <summary>
    /// Migrations that still need applying. Checks for the history table first, because asking EF
    /// for applied migrations on a brand-new database makes it log a (harmless) failed query.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetPendingMigrationsAsync(AppDbContext db, CancellationToken ct)
    {
        if (!await TableExistsAsync(db, "__EFMigrationsHistory", ct))
            return db.Database.GetMigrations().ToList();

        return (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
    }

    /// <summary>
    /// Creates the (empty) migration-history table on a brand-new database, so EF's own probe of it
    /// during Migrate() does not log a spurious failed query on the very first boot.
    /// </summary>
    public static async Task EnsureHistoryTableAsync(AppDbContext db, CancellationToken ct)
    {
        if (await TableExistsAsync(db, "__EFMigrationsHistory", ct)) return;
        await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetCreateIfNotExistsScript(), ct);
    }

    /// <summary>True when application tables exist but the Baseline migration was never recorded.</summary>
    public static async Task<bool> IsLegacyDatabaseAsync(AppDbContext db, CancellationToken ct)
    {
        if (await TableExistsAsync(db, "__EFMigrationsHistory", ct))
        {
            var applied = await db.Database.GetAppliedMigrationsAsync(ct);
            if (applied.Contains(BaselineMigrationId)) return false;
        }

        return await TableExistsAsync(db, "Cases", ct);
    }

    /// <summary>Upgrades and adopts the legacy database atomically: all or nothing.</summary>
    public static async Task AdoptAsync(AppDbContext db, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            foreach (var statement in LegacySchemaUpgrade.Statements)
                await db.Database.ExecuteSqlRawAsync(statement, ct);

            foreach (var statement in LegacySchemaUpgrade.ConvergenceStatements)
                await db.Database.ExecuteSqlRawAsync(statement, ct);

            await TightenNullabilityAsync(db, ct);

            await db.Database.ExecuteSqlRawAsync(SchemaExtras.UpSql, ct);
            await db.Database.ExecuteSqlRawAsync(SchemaExtras.SyncCaseNumberSequenceSql, ct);

            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
            await db.Database.ExecuteSqlRawAsync(
                history.GetInsertScript(new HistoryRow(BaselineMigrationId, EfProductVersion())), ct);

            await tx.CommitAsync(ct);
        });
    }

    /// <summary>
    /// Columns the old bootstrap added with ALTER TABLE … ADD COLUMN … DEFAULT x are nullable, while
    /// the model declares them required. For each such column: fill NULLs with the column default,
    /// then make it NOT NULL. A column without a default is tightened only if it already contains no
    /// NULLs (we cannot invent a value, and never delete or rewrite data to make it fit).
    /// Driven by the EF model, so it covers every column without a hand-maintained list.
    /// </summary>
    private static async Task TightenNullabilityAsync(AppDbContext db, CancellationToken ct)
    {
        var columns = new List<(string Table, string Column, string? Default, bool Nullable)>();
        var conn = db.Database.GetDbConnection();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = "SELECT table_name, column_name, column_default, is_nullable = 'YES' " +
                              "FROM information_schema.columns WHERE table_schema = 'public'";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3)));
        }

        var actual = columns.ToDictionary(c => (c.Table, c.Column));

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table == null) continue;

            foreach (var property in entity.GetProperties())
            {
                if (property.IsNullable || property.IsPrimaryKey()) continue;
                var column = property.GetColumnName();
                if (!actual.TryGetValue((table, column), out var info) || !info.Nullable) continue;
                if (info.Default != null)
                {
                    await db.Database.ExecuteSqlRawAsync(
                        $"UPDATE \"{table}\" SET \"{column}\" = DEFAULT WHERE \"{column}\" IS NULL", ct);
                }
                else
                {
                    // No default to fill with: tighten only if the data already complies.
                    var hasNulls = await db.Database
                        .SqlQueryRaw<bool>($"SELECT EXISTS (SELECT 1 FROM \"{table}\" WHERE \"{column}\" IS NULL) AS \"Value\"")
                        .SingleAsync(ct);
                    if (hasNulls) continue;
                }
                await db.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{table}\" ALTER COLUMN \"{column}\" SET NOT NULL", ct);
            }
        }
    }

    private static string EfProductVersion() =>
        (typeof(DbContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "10.0.0")
        .Split('+')[0];
}
