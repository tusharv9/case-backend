namespace CaseManagement.Api.Services;

using CaseManagement.Api.Configuration;
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
        if (!File.Exists(path)) throw new FileNotFoundException("The stored file for this attachment is missing from the server.");
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
