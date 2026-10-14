using CaseManagement.Api.Configuration;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;

namespace CaseManagement.Tests;

/// <summary>
/// Integration tests against a REAL PostgreSQL server (the InMemory provider cannot exercise
/// migrations, raw SQL, sequences, indexes or constraints). They create and drop their own
/// throw-away databases, so any scratch server works — never point this at a real environment.
///
/// Run them by setting TEST_POSTGRES_ADMIN_CONNECTION to a connection string for a role that may
/// CREATE DATABASE, without a Database part, e.g.
///   TEST_POSTGRES_ADMIN_CONNECTION="Host=127.0.0.1;Port=5432;Username=postgres"
/// Without it the tests are reported as skipped.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string EnvVar = "TEST_POSTGRES_ADMIN_CONNECTION";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Set {EnvVar} to run real-PostgreSQL integration tests.";
    }
}

/// <summary>A uniquely named database that is dropped when disposed.</summary>
public sealed class TempDatabase : IAsyncDisposable
{
    private readonly string _adminConnection;
    public string Name { get; }
    public string ConnectionString { get; }

    private TempDatabase(string adminConnection, string name)
    {
        _adminConnection = adminConnection;
        Name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name }.ConnectionString;
    }

    public static async Task<TempDatabase> CreateAsync()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.EnvVar)!;
        var name = "cmtest_" + Guid.NewGuid().ToString("N");
        await using var conn = new NpgsqlConnection(admin);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        return new TempDatabase(admin, name);
    }

    public AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_adminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

public class PostgresIntegrationTests
{
    private static (DatabaseInitializer Initializer, DatabaseInitializationState State) CreateInitializer(
        TempDatabase db, bool migrateOnStartup, SeedMode seed, int connectTimeoutSeconds = 30)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(db.ConnectionString));
        var provider = services.BuildServiceProvider();

        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Production");

        var state = new DatabaseInitializationState();
        var initializer = new DatabaseInitializer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DatabaseOptions
            {
                MigrateOnStartup = migrateOnStartup,
                Seed = seed,
                ConnectTimeoutSeconds = connectTimeoutSeconds
            }),
            env.Object,
            state,
            NullLogger<DatabaseInitializer>.Instance);

        return (initializer, state);
    }

    [PostgresFact]
    public async Task FreshDatabase_IsMigrated_AndCreatesSequenceAndNonModelIndexes()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (initializer, state) = CreateInitializer(db, migrateOnStartup: true, SeedMode.None);

        Assert.True(await initializer.RunAsync(forceMigrate: false, CancellationToken.None));
        Assert.Equal(DatabaseInitializationStatus.Ready, state.Status);

        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT nextval('{SchemaConstants.CaseNumberSequence}')"));
        Assert.Equal(1L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'IX_Customers_NRIC_Normalized'"));
        Assert.Equal(1L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%Baseline'"));
    }

    [PostgresFact]
    public async Task BootstrapSeed_CreatesRequiredConfiguration_ButNoUsersTeamsOrCustomers()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (initializer, _) = CreateInitializer(db, migrateOnStartup: true, SeedMode.Bootstrap);

        Assert.True(await initializer.RunAsync(false, CancellationToken.None));

        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM \"LookupValues\"") > 0);
        Assert.Equal(7L, await db.ScalarAsync<long>("SELECT count(*) FROM \"BusinessHours\""));
        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM \"PrioritySlaRules\"") > 0);
        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM \"EscalationLevelConfigs\"") > 0);

        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM \"Users\""));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM \"Departments\""));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM \"RoutingRules\""));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM \"Customers\""));
    }

    [PostgresFact]
    public async Task SeedSteps_RunOnce_SoDeletedDataIsNeverResurrected()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (first, _) = CreateInitializer(db, true, SeedMode.Development);
        Assert.True(await first.RunAsync(false, CancellationToken.None));

        var usersAfterSeed = await db.ScalarAsync<long>("SELECT count(*) FROM \"Users\"");
        Assert.True(usersAfterSeed > 0);

        // An administrator deletes seeded configuration and data...
        await using (var ctx = db.NewContext())
        {
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM \"RoutingRules\"");
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM \"EscalationLevelConfigs\"");
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM \"LookupValues\" WHERE \"TypeCode\" = 'DASHBOARD_QUICK_ACTION'");
        }

        // ...and the application restarts.
        var (second, _) = CreateInitializer(db, true, SeedMode.Development);
        Assert.True(await second.RunAsync(false, CancellationToken.None));

        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM \"RoutingRules\""));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM \"EscalationLevelConfigs\""));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM \"LookupValues\" WHERE \"TypeCode\" = 'DASHBOARD_QUICK_ACTION'"));
        Assert.Equal(usersAfterSeed, await db.ScalarAsync<long>("SELECT count(*) FROM \"Users\""));
    }

    [PostgresFact]
    public async Task LegacyDatabase_IsAdopted_KeepingEveryRow_AndSeedsNothing()
    {
        await using var db = await TempDatabase.CreateAsync();

        // A legacy database = exactly the Baseline schema (what the old startup code produced) with NO
        // migration history. Build it by applying only the Baseline migration, then forgetting that it ran.
        var deptId = Guid.NewGuid();
        await using (var legacy = db.NewContext())
        {
            var baseline = LegacySchemaAdopter.BaselineMigrationId;
            await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                .GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(legacy)
                .MigrateAsync(baseline);
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\"");
            await legacy.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Departments\" (\"Id\",\"Name\",\"Code\",\"Function\",\"Channels\",\"IsActive\",\"CreatedAt\") " +
                $"VALUES ('{deptId}','Legacy Dept','LD','','Voice',true,NOW());");
            await legacy.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Users\" (\"Id\",\"Name\",\"Email\",\"Role\",\"Status\",\"DepartmentId\",\"CreatedAt\") " +
                $"VALUES ('{Guid.NewGuid()}','Legacy User','legacy@example.test','Agent',0,'{deptId}',NOW());");
        }
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_tables WHERE tablename = '__EFMigrationsHistory'"));

        var (initializer, state) = CreateInitializer(db, migrateOnStartup: true, SeedMode.Development);
        Assert.True(await initializer.RunAsync(false, CancellationToken.None));
        Assert.Equal(DatabaseInitializationStatus.Ready, state.Status);

        // Baseline adopted, later migrations applied; existing rows untouched; and, crucially,
        // nothing was seeded into it.
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%Baseline'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%AddHostUserProjection'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%UnifyPriorities'"));
        // The pre-existing user is linked to the Host-id scheme and stays active.
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM \"Users\" WHERE \"ExternalUserId\" = \"Id\"::text AND \"IsActive\""));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM \"Users\""));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM \"Departments\""));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM \"LookupValues\""));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM \"RoutingRules\""));
        // Membership: the legacy user was eligible through their home team, so they now have a real, assignable membership row
        // (nobody silently drops out of rotation), and the global assignment settings exist as visible data.
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM \"TeamMembers\" WHERE \"DepartmentId\" = '{deptId}' AND \"IsActive\" AND \"IsAssignable\""));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM \"AssignmentConfigurations\" WHERE \"DepartmentId\" IS NULL AND \"Algorithm\" = 'RoundRobin'"));
        // The seed steps are recorded as "adopted" so they can never run later either.
        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM \"SeedHistory\"") >= 10);

        // Idempotent: a second start changes nothing.
        var (again, _) = CreateInitializer(db, true, SeedMode.Development);
        Assert.True(await again.RunAsync(false, CancellationToken.None));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM \"LookupValues\""));
    }

    [PostgresFact]
    public async Task LegacyDatabase_WithAutomaticMigrationDisabled_ReportsMigrationRequired_AndChangesNothing()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using (var legacy = db.NewContext())
        {
            await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                .GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(legacy)
                .MigrateAsync(LegacySchemaAdopter.BaselineMigrationId);
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\"");
        }

        var (initializer, state) = CreateInitializer(db, migrateOnStartup: false, SeedMode.Bootstrap);
        Assert.False(await initializer.RunAsync(false, CancellationToken.None));

        Assert.Equal(DatabaseInitializationStatus.MigrationRequired, state.Status);
        Assert.False(state.IsReady);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_tables WHERE tablename = '__EFMigrationsHistory'"));
    }

    [PostgresFact]
    public async Task UnreachableDatabase_FailsCleanly_AfterTheConfiguredTimeout()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (initializer, state) = CreateInitializer(db, true, SeedMode.None, connectTimeoutSeconds: 1);

        // Drop the database out from under the initializer.
        await db.DisposeAsync();

        Assert.False(await initializer.RunAsync(false, CancellationToken.None));
        Assert.Equal(DatabaseInitializationStatus.Failed, state.Status);
    }

    [PostgresFact]
    public async Task DeletingSubCategory_InUseByCases_IsRejected_AndUnusedOneCleansItsPriorityMapping()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (initializer, _) = CreateInitializer(db, true, SeedMode.None);
        Assert.True(await initializer.RunAsync(false, CancellationToken.None));

        var deptId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var usedSub = Guid.NewGuid();
        var freeSub = Guid.NewGuid();

        await using var ctx = db.NewContext();
        var rule = new PrioritySlaRule { Id = Guid.NewGuid(), Priority = "High", DisplayOrder = 1, CreatedAt = DateTime.UtcNow };
        ctx.AddRange(
            new Department { Id = deptId, Name = "D", Code = "D", CreatedAt = DateTime.UtcNow },
            rule);
        await ctx.SaveChangesAsync();
        ctx.AddRange(
            new User { Id = userId, Name = "U", Email = "u@example.test", Role = "Agent", DepartmentId = deptId, CreatedAt = DateTime.UtcNow },
            new Customer { Id = customerId, FullName = "C", PhoneNumber = "+60 1", PreferredLanguage = "English", CreatedAt = DateTime.UtcNow },
            new DepartmentSubCategory { Id = usedSub, DepartmentId = deptId, Name = "Used", Code = "U", IsActive = true, CreatedAt = DateTime.UtcNow },
            new DepartmentSubCategory { Id = freeSub, DepartmentId = deptId, Name = "Free", Code = "F", IsActive = true, CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
        ctx.AddRange(
            new PriorityCategoryMapping { Id = Guid.NewGuid(), PrioritySlaRuleId = rule.Id, DepartmentSubCategoryId = freeSub, CreatedAt = DateTime.UtcNow },
            new Case
            {
                Id = Guid.NewGuid(), CaseNumber = "C-00001", CaseType = "Complaint", Title = "t", Description = "d",
                Status = CaseStatus.Open, Severity = "High", Subcategory = "Used", DepartmentId = deptId,
                CustomerId = customerId, OwnerId = userId, CreatedAt = DateTime.UtcNow
            });
        await ctx.SaveChangesAsync();

        var service = new ConfigurableSettingsService(
            new ConfigurableSettingsRepository(ctx), ctx,
            new Mock<INotificationService>().Object, new Mock<IHttpContextAccessor>().Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteSubCategoryAsync(usedSub));
        Assert.Contains("1 case(s)", ex.Message);
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM \"DepartmentSubCategories\" WHERE \"Id\" = '{usedSub}'"));

        Assert.True(await service.DeleteSubCategoryAsync(freeSub));
        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM \"PriorityCategoryMappings\" WHERE \"DepartmentSubCategoryId\" = '{freeSub}'"));
    }
}
