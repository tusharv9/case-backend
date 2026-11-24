using System.Text;
using CaseManagement.Api.Configuration;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.EmailIntake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>A received email shown as it was sent, safely; and stored files readable from every server sharing the database.</summary>
/// <remarks>
/// <para>Intake kept only an email's text, which is why signatures, footers and logos were lost on the case. The original .eml
/// still has them, so the case renders from it — through an allow-list sanitizer, because the HTML comes from outside the bank:
/// a script, an event handler or a <c>javascript:</c> link must never survive, while the tables and inline styles that make up a
/// signature must.</para>
/// <para>Files were kept on the disk of the server that received them, so any other server sharing the database answered 404.
/// The database store fixes that while still reading what is only on the local disk, and copies it in.</para>
/// </remarks>
public class EmailBodyAndFileStoreTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "case-files-" + Guid.NewGuid().ToString("N"));
    private readonly DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"files-{Guid.NewGuid()}").Options;

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private LocalDiskAttachmentStore Disk() =>
        new(Options.Create(new AttachmentOptions { StoragePath = folder }), new Env(folder), NullLogger<LocalDiskAttachmentStore>.Instance);

    private DatabaseAttachmentStore Store() => new(new Factory(options), Disk());

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    private static async Task<string> ReadAll(Stream s) { using var r = new StreamReader(s); return await r.ReadToEndAsync(); }

    // ------------------------------------------------------------------------------------------------ the shared file store

    [Fact]
    public async Task A_file_saved_on_one_server_is_read_on_another_that_shares_the_database()
    {
        var key = await Store().SaveAsync(Bytes("invoice"), ".pdf");

        // A second server: same database, its own (empty) disk.
        var otherServer = new DatabaseAttachmentStore(new Factory(options),
            new LocalDiskAttachmentStore(Options.Create(new AttachmentOptions { StoragePath = folder + "-other" }), new Env(folder), NullLogger<LocalDiskAttachmentStore>.Instance));

        Assert.Equal("invoice", await ReadAll(await otherServer.OpenReadAsync(key)));
    }

    [Fact]
    public async Task A_file_saved_on_disk_before_the_switch_still_opens_and_is_copied_in_once()
    {
        var key = await Disk().SaveAsync(Bytes("old file"), ".txt");

        Assert.Equal("old file", await ReadAll(await Store().OpenReadAsync(key)));
        Assert.True(await Store().ImportAsync(key));
        Assert.False(await Store().ImportAsync(key));   // idempotent
        await using var db = new AppDbContext(options);
        Assert.Equal(8, (await db.StoredFiles.SingleAsync(f => f.Key == key)).Length);
    }

    [Fact]
    public async Task A_file_on_no_server_says_so_instead_of_a_bare_404()
    {
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => Store().OpenReadAsync($"{Guid.NewGuid()}.pdf"));

        Assert.Contains("not stored on this server", error.Message);
    }

    // ------------------------------------------------------------------------------------------------ email rendering

    [Fact]
    public void Script_event_handlers_and_javascript_links_never_survive_but_a_signature_table_does()
    {
        var html = "<html><head><title>T</title><style>.sig{color:#123456}</style></head><body>"
                   + "<p onclick=\"alert(1)\">Hello</p><script>alert(2)</script><a href=\"javascript:alert(3)\">x</a>"
                   + "<iframe src=\"https://evil\"></iframe><form><input></form>"
                   + "<table class=\"sig\" style=\"border:1px solid #ccc\"><tr><td><b>Ann Lee</b><br>RMA Bank</td></tr></table></body></html>";

        var clean = EmailBodyRenderer.Sanitize(html);

        Assert.DoesNotContain("alert", clean);
        Assert.DoesNotContain("onclick", clean);
        Assert.DoesNotContain("javascript:", clean);
        Assert.DoesNotContain("<iframe", clean);
        Assert.DoesNotContain("<input", clean);
        Assert.DoesNotContain(">T<", clean);   // the <title> text does not leak into the body
        Assert.Contains("<table", clean);
        Assert.Contains("border", clean);
        Assert.Contains("Ann Lee", clean);
        Assert.Contains(".sig", clean);      // the signature's own stylesheet is kept
    }

    private async Task<InboundEmail> StoreEmail(MimeMessage message, string verification = SenderVerification.Verified)
    {
        using var raw = new MemoryStream();
        await message.WriteToAsync(raw);
        raw.Position = 0;
        var key = await Store().SaveAsync(raw, ".eml");
        return new InboundEmail { Id = Guid.NewGuid(), RawKey = key, BodyText = "plain text", Verification = verification };
    }

    private EmailBodyRenderer Renderer() => new(Store(), new MemoryCache(new MemoryCacheOptions()), NullLogger<EmailBodyRenderer>.Instance);

    private static MimeMessage WithSignature(string html, params MimePart[] inline)
    {
        var builder = new BodyBuilder { TextBody = "Hello", HtmlBody = html };
        foreach (var part in inline) builder.LinkedResources.Add(part);
        return new MimeMessage { Subject = "s", Body = builder.ToMessageBody() };
    }

    private static MimePart Inline(string contentId, string type, byte[] content) =>
        new(type) { ContentId = contentId, Content = new MimeContent(new MemoryStream(content)), ContentTransferEncoding = ContentEncoding.Base64 };

    [Fact]
    public async Task The_signature_logo_embedded_in_the_email_is_shown_inline()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var email = await StoreEmail(WithSignature("<p>Hi</p><img src=\"cid:logo@bank\" alt=\"logo\">", Inline("logo@bank", "image/png", png)));

        var body = await Renderer().RenderAsync(email);

        Assert.Equal("html", body.Format);
        Assert.Contains("data:image/png;base64," + Convert.ToBase64String(png), body.Html);
        Assert.Equal(0, body.InlineImagesOmitted);
        Assert.Equal("Hello", body.Text);
    }

    [Fact]
    public async Task An_embedded_SVG_is_never_shown_as_it_can_carry_script()
    {
        var email = await StoreEmail(WithSignature("<img src=\"cid:x\">", Inline("x", "image/svg+xml", Encoding.UTF8.GetBytes("<svg onload='alert(1)'/>"))));

        var body = await Renderer().RenderAsync(email);

        Assert.DoesNotContain("svg", body.Html);
        Assert.Equal(1, body.InlineImagesOmitted);
    }

    [Fact]
    public async Task Pictures_from_the_internet_are_flagged_and_shown_by_default_only_for_verified_senders()
    {
        var html = "<img src=\"https://cdn.bank.example/logo.png\">";

        var verified = await Renderer().RenderAsync(await StoreEmail(WithSignature(html)));
        var unknown = await Renderer().RenderAsync(await StoreEmail(WithSignature(html), SenderVerification.NotVerified));

        Assert.True(verified.HasRemoteImages);
        Assert.True(verified.ShowRemoteImagesByDefault);
        Assert.True(unknown.HasRemoteImages);
        Assert.False(unknown.ShowRemoteImagesByDefault);
    }

    [Fact]
    public async Task An_email_without_HTML_or_without_its_original_falls_back_to_its_text()
    {
        var textOnly = await StoreEmail(new MimeMessage { Subject = "s", Body = new TextPart("plain") { Text = "Just text\nThanks" } });
        var noOriginal = new InboundEmail { Id = Guid.NewGuid(), BodyText = "kept text" };

        var a = await Renderer().RenderAsync(textOnly);
        var b = await Renderer().RenderAsync(noOriginal);

        Assert.Equal("text", a.Format);
        Assert.Equal("Just text\nThanks", a.Text);
        Assert.Equal("text", b.Format);
        Assert.Equal("kept text", b.Text);
        Assert.NotNull(b.Note);
    }
}
