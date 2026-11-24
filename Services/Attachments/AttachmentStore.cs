namespace CaseManagement.Api.Services;

using CaseManagement.Api.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Where attachment bytes live. Everything else (validation, authorisation, the database record) is independent of this,
/// so moving to object storage later means adding one implementation of this interface.
/// </summary>
public interface IAttachmentStore
{
    /// <summary>Stores the content and returns the key to read it back with.</summary>
    Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public class LocalDiskAttachmentStore : IAttachmentStore
{
    private readonly string _root;

    public LocalDiskAttachmentStore(IOptions<AttachmentOptions> options, IHostEnvironment env, ILogger<LocalDiskAttachmentStore> logger)
    {
        var configured = options.Value.StoragePath;
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Path.Combine(env.ContentRootPath, "Uploads", "Attachments") : configured);

        if (env.IsProduction() && string.IsNullOrWhiteSpace(configured))
            logger.LogWarning(
                "Attachments are stored under the application folder ({Root}). On a host with an ephemeral file system they are LOST on every deploy or restart. " +
                "Set Attachments:StoragePath to a persistent volume.", _root);
    }

    private string PathFor(string key)
    {
        // A key is a plain file name this class generated; anything else (traversal, separators) is refused.
        if (string.IsNullOrWhiteSpace(key) || key != Path.GetFileName(key) || key.Contains(".."))
            throw new ArgumentException("Invalid attachment key.");
        return Path.Combine(_root, key);
    }

    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var key = $"{Guid.NewGuid()}{extension}";
        await using var file = new FileStream(PathFor(key), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(file, ct);
        return key;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) throw new FileNotFoundException("This file is not stored on this server: it is still only on the server that received it. It opens here once that server has run the current version (which copies its files into shared storage).");
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Files kept in the shared database (<see cref="Models.StoredFile"/>), so any server sharing it can read them — the fix for
/// "This file is not stored on this server" (404). A file not in the database is still looked for on this server's disk,
/// where everything was saved before; <see cref="LocalFileImporter"/> copies those in.
/// </summary>
public sealed class DatabaseAttachmentStore(IDbContextFactory<Data.AppDbContext> contexts, LocalDiskAttachmentStore localDisk) : IAttachmentStore
{
    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var key = $"{Guid.NewGuid()}{extension}";
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.StoredFiles.Add(new Models.StoredFile { Key = key, Content = buffer.ToArray(), Length = buffer.Length });
        await db.SaveChangesAsync(ct);
        return key;
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var bytes = await db.StoredFiles.AsNoTracking().Where(f => f.Key == key).Select(f => f.Content).FirstOrDefaultAsync(ct);
        if (bytes != null) return new MemoryStream(bytes, writable: false);
        return await localDisk.OpenReadAsync(key, ct);   // saved before the switch; throws "not stored on this server" if absent here too
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.StoredFiles.Where(f => f.Key == key).ExecuteDeleteAsync(ct);
        await localDisk.DeleteAsync(key, ct);
    }

    /// <summary>Copies a file from this server's disk into the database, unless it is there already. True when it was copied.</summary>
    public async Task<bool> ImportAsync(string key, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.StoredFiles.AnyAsync(f => f.Key == key, ct)) return false;
        Stream source;
        try { source = await localDisk.OpenReadAsync(key, ct); }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return false; }
        await using (source)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, ct);
            db.StoredFiles.Add(new Models.StoredFile { Key = key, Content = buffer.ToArray(), Length = buffer.Length });
            await db.SaveChangesAsync(ct);
        }
        return true;
    }
}

/// <summary>
/// Once at startup: every file the database refers to (case attachments, ticket files, received emails) that is only on THIS
/// server's disk is copied into the database. Each server that received files before the switch does this when it first runs
/// the new version; it is idempotent, so it costs nothing afterwards.
/// </summary>
public sealed class LocalFileImporter(IServiceScopeFactory scopes, IOptions<AttachmentOptions> options, ILogger<LocalFileImporter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ImportLocalFilesOnStartup) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);   // after migrations and warm-up
            using var scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetRequiredService<IAttachmentStore>() is not DatabaseAttachmentStore store) return;
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();

            var keys = new List<string>();
            keys.AddRange(await db.CaseAttachments.AsNoTracking().Select(a => a.StoragePath).ToListAsync(stoppingToken));
            keys.AddRange(await db.TicketAttachments.AsNoTracking().Select(a => a.StorageKey).ToListAsync(stoppingToken));
            keys.AddRange(await db.InboundEmails.AsNoTracking().Where(e => e.RawKey != null).Select(e => e.RawKey!).ToListAsync(stoppingToken));
            var stored = (await db.StoredFiles.AsNoTracking().Select(f => f.Key).ToListAsync(stoppingToken)).ToHashSet();

            var copied = 0;
            foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k) && !stored.Contains(k)).Distinct())
            {
                try { if (await store.ImportAsync(key, stoppingToken)) copied++; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not copy stored file {Key} into the database.", key);
                }
            }
            if (copied > 0) logger.LogInformation("Copied {Count} file(s) from this server's disk into the shared database.", copied);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Copying this server's files into the database did not finish; it runs again on the next start.");
        }
    }
}
