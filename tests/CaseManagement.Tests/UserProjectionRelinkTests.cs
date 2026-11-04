using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaseManagement.Tests;

/// <summary>
/// A local user row can lose its Host link while keeping the placeholder address made from that Host user's id. The next
/// sign-in used to insert a second row with the same address, which the unique email index refused, so every Case Management
/// request answered 409 "That already exists" for that person. The row is theirs: it must be linked again, not duplicated.
/// </summary>
public class UserProjectionRelinkTests : IDisposable
{
    private readonly AppDbContext db;

    private sealed class EmptyDirectory : IHostUserDirectory
    {
        public List<HostUserInfo> Users { get; } = new();
        public Task<HostUserInfo?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Users.FirstOrDefault(u => u.ExternalUserId == id));
        public Task<IReadOnlyList<HostUserInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostUserInfo>>(Users.ToList());
    }

    public UserProjectionRelinkTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"relink-{Guid.NewGuid()}").Options;
        db = new AppDbContext(options);
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private Guid AddOrphan(string externalUserId)
    {
        var id = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = id, ExternalUserId = null, Name = "Super Admin", Email = UserProjectionService.PlaceholderEmail(externalUserId),
            IsActive = false, CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return id;
    }

    [Fact]
    public async Task A_host_user_whose_unlinked_row_carries_their_placeholder_address_is_linked_to_it_again()
    {
        var orphanId = AddOrphan("host-1");
        db.Users.Add(new User { Id = Guid.NewGuid(), ExternalUserId = "someone-else", Name = "Super Admin", Email = "admin@x.test", IsActive = true });
        await db.SaveChangesAsync();
        var service = new UserProjectionService(db, new EmptyDirectory(), NullLogger<UserProjectionService>.Instance);

        var projected = await service.EnsureAsync(new HostPrincipal("host-1", "Super Admin", "admin@x.test", Array.Empty<string>(), Array.Empty<string>()), provisionIfMissing: true, CancellationToken.None);

        Assert.NotNull(projected);
        Assert.Equal(orphanId, projected!.LocalId);
        Assert.True(projected.IsActive);
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Equal("host-1", (await db.Users.SingleAsync(u => u.Id == orphanId)).ExternalUserId);
    }

    [Fact]
    public async Task The_directory_sync_relinks_such_a_row_instead_of_adding_a_second_one()
    {
        var orphanId = AddOrphan("host-2");
        var directory = new EmptyDirectory();
        directory.Users.Add(new HostUserInfo("host-2", "Super Admin", null, "Administrator", true));

        var (created, updated, _) = await new HostUserSynchronizer(db, directory, Options.Create(new HostIntegrationOptions())).SyncAllAsync(CancellationToken.None);

        Assert.Equal((0, 1), (created, updated));
        var row = await db.Users.SingleAsync();
        Assert.Equal(orphanId, row.Id);
        Assert.Equal("host-2", row.ExternalUserId);
        Assert.True(row.IsActive);
    }
}
