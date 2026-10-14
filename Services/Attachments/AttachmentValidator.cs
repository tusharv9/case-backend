namespace CaseManagement.Api.Services;

using CaseManagement.Api.Configuration;

/// <summary>What the server decided a file is — never what the client claimed.</summary>
public sealed record AttachmentVerdict(string Extension, string ContentType, string DisplayName);

/// <summary>
/// Decides whether an uploaded file is acceptable, from its NAME, SIZE and the first bytes of its CONTENT. A file must
/// be what its extension says it is: a program renamed to ".pdf" is rejected, and so is a "text" file that is really a
/// web page or script. The content type served back on download comes from the verified extension, not from the client.
/// </summary>
public static class AttachmentValidator
{
    private static readonly byte[] Ole = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
    private static readonly byte[] Zip = { 0x50, 0x4B, 0x03, 0x04 };

    private sealed record Kind(string ContentType, byte[][]? Signatures);   // Signatures null = must be plain text

    private static readonly IReadOnlyDictionary<string, Kind> Kinds = new Dictionary<string, Kind>
    {
        [".pdf"] = new("application/pdf", new[] { "%PDF-"u8.ToArray() }),
        [".png"] = new("image/png", new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } }),
        [".jpg"] = new("image/jpeg", new[] { new byte[] { 0xFF, 0xD8, 0xFF } }),
        [".jpeg"] = new("image/jpeg", new[] { new byte[] { 0xFF, 0xD8, 0xFF } }),
        [".gif"] = new("image/gif", new[] { "GIF87a"u8.ToArray(), "GIF89a"u8.ToArray() }),
        [".bmp"] = new("image/bmp", new[] { "BM"u8.ToArray() }),
        [".zip"] = new("application/zip", new[] { Zip }),
        [".docx"] = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document", new[] { Zip }),
        [".xlsx"] = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", new[] { Zip }),
        [".7z"] = new("application/x-7z-compressed", new[] { new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C } }),
        [".rar"] = new("application/vnd.rar", new[] { "Rar!"u8.ToArray() }),
        [".doc"] = new("application/msword", new[] { Ole }),
        [".xls"] = new("application/vnd.ms-excel", new[] { Ole }),
        [".msg"] = new("application/vnd.ms-outlook", new[] { Ole }),
        [".txt"] = new("text/plain", null),
        [".csv"] = new("text/csv", null),
        [".eml"] = new("message/rfc822", null),
    };

    /// <summary>The extensions the content check can verify. Anything else can never be uploaded.</summary>
    public static IReadOnlyCollection<string> SupportedExtensions => Kinds.Keys.ToList();

    private static readonly byte[][] ExecutableSignatures =
    {
        "MZ"u8.ToArray(),                                   // Windows / DOS programs and DLLs
        new byte[] { 0x7F, 0x45, 0x4C, 0x46 },              // ELF
        new byte[] { 0xCF, 0xFA, 0xED, 0xFE },              // Mach-O
        new byte[] { 0xCE, 0xFA, 0xED, 0xFE },
        new byte[] { 0xCA, 0xFE, 0xBA, 0xBE },              // Mach-O fat / Java class
        "#!"u8.ToArray(),                                   // scripts with an interpreter line
    };

    private static readonly string[] ActiveContentPrefixes = { "<!doctype", "<html", "<svg", "<script", "<?xml", "<?php", "<iframe", "<body" };

    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".sh", ".bash", ".dll", ".so", ".dylib", ".vbs", ".ps1", ".jar", ".com", ".scr", ".msi", ".pif",
        ".application", ".gadget", ".hta", ".cpl", ".msc", ".msp", ".svg", ".html", ".htm", ".js", ".php", ".py", ".xhtml"
    };

    /// <exception cref="ArgumentException">The file is not acceptable; the message says why.</exception>
    public static AttachmentVerdict Validate(string? clientFileName, long length, ReadOnlySpan<byte> head, AttachmentOptions options)
    {
        if (length <= 0) throw new ArgumentException("Please select a valid non-empty file to upload.");

        var maxSize = options.MaxFileSizeBytes > 0 ? options.MaxFileSizeBytes : 25 * 1024 * 1024;
        if (length > maxSize)
            throw new ArgumentException($"File size ({length / (1024 * 1024)} MB) exceeds allowed limit of {maxSize / (1024 * 1024)} MB.");

        var name = DisplayName(clientFileName);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) throw new ArgumentException("Files without an extension are not permitted.");
        if (DangerousExtensions.Contains(ext)) throw new ArgumentException($"File extension '{ext}' is forbidden for security reasons.");

        var allowed = options.GetAllowedExtensionSet();
        if (allowed.Count > 0 && !allowed.Contains(ext))
            throw new ArgumentException($"File type '{ext}' is not permitted. Allowed extensions: {options.AllowedExtensions}");

        if (!Kinds.TryGetValue(ext, out var kind))
            throw new ArgumentException($"File type '{ext}' cannot be verified and is not accepted. Supported types: {string.Join(", ", Kinds.Keys.OrderBy(k => k))}.");

        foreach (var sig in ExecutableSignatures)
            if (head.StartsWith(sig)) throw new ArgumentException("This file is a program or script, whatever its name says, and cannot be attached.");

        if (kind.Signatures == null)
        {
            if (head.IndexOf((byte)0) >= 0) throw new ArgumentException($"This file is not a text file, although it is named '{ext}'.");
            var text = System.Text.Encoding.UTF8.GetString(head).TrimStart('﻿', ' ', '\t', '\r', '\n').ToLowerInvariant();
            if (ActiveContentPrefixes.Any(p => text.StartsWith(p)))
                throw new ArgumentException("This file contains web page or script content and cannot be attached as text.");
        }
        else if (!MatchesAny(head, kind.Signatures))
        {
            throw new ArgumentException($"The contents of this file do not match its '{ext}' extension, so it was not accepted.");
        }

        return new AttachmentVerdict(ext, kind.ContentType, name);
    }

    private static bool MatchesAny(ReadOnlySpan<byte> head, byte[][] signatures)
    {
        foreach (var sig in signatures) if (head.StartsWith(sig)) return true;
        return false;
    }

    /// <summary>The name to show: no path, no control characters, at most 200 characters.</summary>
    public static string DisplayName(string? clientFileName)
    {
        var name = Path.GetFileName((clientFileName ?? string.Empty).Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Invalid file name.");
        if (name.Length > 200)
        {
            var ext = Path.GetExtension(name);
            name = name[..(200 - ext.Length)] + ext;
        }
        return name;
    }
}
