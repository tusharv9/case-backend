namespace CaseManagement.Api.Services;

using System.Security.Cryptography;
using CaseManagement.Api.Configuration;
using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public interface IAttachmentService
{
    Task<IEnumerable<CaseAttachmentDto>> GetAttachmentsAsync(Guid caseId, CancellationToken ct = default);
    Task<CaseAttachmentDto> UploadAttachmentAsync(Guid caseId, IFormFile file, string? note, Guid userId, CancellationToken ct = default);
    Task<AttachmentDownload> OpenDownloadAsync(Guid caseId, Guid attachmentId, CancellationToken ct = default);
}

/// <summary>A verified file ready to stream back. The caller owns (and disposes) <see cref="Content"/>.</summary>
public sealed record AttachmentDownload(Stream Content, string ContentType, string FileName, string? Sha256);

public class AttachmentService : IAttachmentService
{
    private const int HeadBytes = 8192;

    private readonly AppDbContext _context;
    private readonly IAttachmentStore _store;
    private readonly AttachmentOptions _options;

    public AttachmentService(AppDbContext context, IAttachmentStore store, IOptions<AttachmentOptions> options)
    {
        _context = context;
        _store = store;
        _options = options.Value;
    }

    public async Task<IEnumerable<CaseAttachmentDto>> GetAttachmentsAsync(Guid caseId, CancellationToken ct = default)
    {
        if (!await _context.Cases.AnyAsync(c => c.Id == caseId, ct)) throw new KeyNotFoundException("Case not found");

        return await _context.CaseAttachments.AsNoTracking()
            .Where(a => a.CaseId == caseId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new CaseAttachmentDto
            {
                Id = a.Id, CaseId = a.CaseId, FileName = a.FileName, FileType = a.FileType, FileSizeBytes = a.FileSizeBytes, Note = a.Note,
                UploadedByUserId = a.UploadedByUserId, UploadedByUserName = a.UploadedByUser != null ? a.UploadedByUser.Name : string.Empty, CreatedAt = a.CreatedAt
            })
            .ToListAsync(ct);
    }

    public async Task<CaseAttachmentDto> UploadAttachmentAsync(Guid caseId, IFormFile file, string? note, Guid userId, CancellationToken ct = default)
    {
        if (file == null || file.Length == 0) throw new ArgumentException("Please select a valid non-empty file to upload.");
        if (!await _context.Cases.AnyAsync(c => c.Id == caseId, ct)) throw new KeyNotFoundException("Case not found");

        // Judge the file by its content, not by what the client says it is.
        await using var input = file.OpenReadStream();
        var head = new byte[(int)Math.Min(HeadBytes, file.Length)];
        var read = 0;
        while (read < head.Length)
        {
            var n = await input.ReadAsync(head.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        var verdict = AttachmentValidator.Validate(file.FileName, file.Length, head.AsSpan(0, read), _options);

        input.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
        input.Position = 0;
        var key = await _store.SaveAsync(input, verdict.Extension, ct);

        var attachment = new CaseAttachment
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            FileName = verdict.DisplayName,
            FileType = verdict.ContentType,
            ContentType = verdict.ContentType,
            FileSizeBytes = file.Length,
            FileSize = file.Length,
            StoragePath = key,
            ContentHash = hash,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            UploadedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            _context.CaseAttachments.Add(attachment);

            var size = file.Length >= 1024 * 1024 ? $"{file.Length / (1024.0 * 1024.0):F1} MB" : $"{file.Length / 1024.0:F0} KB";
            _context.CaseEvents.Add(new CaseEvent
            {
                CaseId = caseId,
                EventType = EventType.Other,
                Message = $"Attached file: {verdict.DisplayName} ({size}).{(attachment.Note == null ? "" : $" Note: {attachment.Note}")}",
                IsInternal = true,
                CreatedAt = DateTime.UtcNow,
                UserId = userId
            });
            await _context.SaveChangesAsync(ct);
        }
        catch
        {
            await _store.DeleteAsync(key, CancellationToken.None);   // no orphaned file when the record could not be saved
            throw;
        }

        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        return new CaseAttachmentDto
        {
            Id = attachment.Id, CaseId = caseId, FileName = attachment.FileName, FileType = attachment.FileType, FileSizeBytes = attachment.FileSizeBytes,
            Note = attachment.Note, UploadedByUserId = userId, UploadedByUserName = user?.Name ?? string.Empty, CreatedAt = attachment.CreatedAt
        };
    }

    public async Task<AttachmentDownload> OpenDownloadAsync(Guid caseId, Guid attachmentId, CancellationToken ct = default)
    {
        // The attachment must belong to THIS case: a valid attachment id under another case's URL is "not found".
        var attachment = await _context.CaseAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId && a.CaseId == caseId, ct)
                         ?? throw new KeyNotFoundException("Attachment not found.");

        var content = await _store.OpenReadAsync(attachment.StoragePath, ct);
        return new AttachmentDownload(content, string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType, attachment.FileName, attachment.ContentHash);
    }
}
