namespace CaseManagement.Api.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>
/// Lets <c>dotnet ef migrations …</c> build the DbContext without booting the whole application
/// (and therefore without touching any database). Set <c>ConnectionStrings__DefaultConnection</c>
/// to target a specific database; otherwise a harmless local placeholder is used, which is enough
/// for generating migrations because generation never connects.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=casemanagement_design;Username=postgres";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}
