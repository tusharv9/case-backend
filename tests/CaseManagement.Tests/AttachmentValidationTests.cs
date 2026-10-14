using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CaseManagement.Api.Configuration;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Attachments are judged by their CONTENT, stored behind an interface, and only ever served back as downloads
/// of the type the server verified.
/// </summary>
public class AttachmentValidationTests : IDisposable
{
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF");
    private static readonly byte[] PngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cm-attach-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private AppDbContext Db() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IFormFile File(string fileName, byte[] content, string clientContentType = "application/pdf") =>
        new FormFile(new MemoryStream(content), 0, content.Length, "file", fileName) { Headers = new HeaderDictionary(), ContentType = clientContentType };

    private AttachmentService Service(AppDbContext db, AttachmentOptions? options = null)
    {
        options ??= new AttachmentOptions();
        options.StoragePath = _dir;
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(_dir);
        env.Setup(e => e.EnvironmentName).Returns("Development");
        var store = new LocalDiskAttachmentStore(Options.Create(options), env.Object, NullLogger<LocalDiskAttachmentStore>.Instance);
        return new AttachmentService(db, store, Options.Create(options));
    }

    private static async Task<(Case Case, User User)> SeedCaseAsync(AppDbContext db)
    {
        var dept = new Department { Id = Guid.NewGuid(), Name = "Support", Code = "SUP" };
        var user = new User { Id = Guid.NewGuid(), Name = "Agent 1", Email = "agent1@bank.com", Role = "Agent", DepartmentId = dept.Id };
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "Customer 1", NRIC = "900101-14-1111", PhoneNumber = "+60123456789" };
        var c = new Case
        {
            Id = Guid.NewGuid(), CaseNumber = $"C-{Random.Shared.Next(1000, 9999)}", Title = "Test Case", CustomerId = customer.Id, Customer = customer,
            DepartmentId = dept.Id, Department = dept, OwnerId = user.Id, Owner = user, Status = CaseStatus.Open, CreatedAt = DateTime.UtcNow
        };
        db.AddRange(dept, user, customer, c);
        await db.SaveChangesAsync();
        return (c, user);
    }

    // ----------------------------------------------------------------------------------------------- rejections

    [Fact]
    public async Task EmptyFile_IsRejected()
    {
        using var db = Db();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).UploadAttachmentAsync(Guid.NewGuid(), File("test.pdf", Array.Empty<byte>()), "note", Guid.NewGuid()));
    }

    [Fact]
    public async Task DangerousExtension_IsRejected()
    {
        using var db = Db();
        var (c, u) = await SeedCaseAsync(db);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).UploadAttachmentAsync(c.Id, File("malware.exe", Encoding.UTF8.GetBytes("MZfake"), "application/x-msdownload"), "x", u.Id));
    }

    [Fact]
    public async Task DisallowedExtension_IsRejected()
    {
        using var db = Db();
        var (c, u) = await SeedCaseAsync(db);
        var svc = Service(db, new AttachmentOptions { AllowedExtensions = ".pdf,.docx,.png" });
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UploadAttachmentAsync(c.Id, File("song.mp3", Encoding.UTF8.GetBytes("sound-data"), "audio/mpeg"), "x", u.Id));
    }

    [Fact]
    public async Task OversizedFile_IsRejected()
    {
        using var db = Db();
        var (c, u) = await SeedCaseAsync(db);
        var svc = Service(db, new AttachmentOptions { MaxFileSizeBytes = 100 });
        var big = PdfBytes.Concat(new byte[200]).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UploadAttachmentAsync(c.Id, File("large.pdf", big), "x", u.Id));
    }

    [Theory]
    [InlineData("invoice.pdf", "MZ\u0090\u0000\u0003\u0000\u0000\u0000")]            // a Windows program renamed .pdf
    [InlineData("notes.txt", "#!/bin/sh\nrm -rf /")]                                  // a script renamed .txt
    [InlineData("notes.txt", "<html><script>alert(1)</script></html>")]               // a web page renamed .txt
    [InlineData("notes.csv", "  <svg xmlns='http://www.w3.org/2000/svg'><script/></svg>")]
    [InlineData("photo.png", "this is not a png at all")]                             // wrong content for the extension
    [InlineData("report.pdf", "just some text")]
    public async Task ContentMustMatchTheExtension(string name, string content)
    {
        using var db = Db();
        var (c, u) = await SeedCaseAsync(db);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).UploadAttachmentAsync(c.Id, File(name, Encoding.Latin1.GetBytes(content)), "x", u.Id));
    }

    [Theory]
    [InlineData("logo.svg")]
    [InlineData("page.html")]
    public async Task ActiveContentTypes_AreNeverAccepted_EvenIfAnAdministratorAllowsThem(string name)
    {
        using var db = Db();
        var (c, u) = await SeedCaseAsync(db);
        var svc = Service(db, new AttachmentOptions { AllowedExtensions = ".svg,.html,.pdf" });
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UploadAttachmentAsync(c.Id, File(name, Encoding.UTF8.GetBytes("<svg></svg>")), "x", u.Id));
    }

    [Fact]
    public void AnExtensionTheCheckCannotVerify_IsRejected()
    {
        var options = new AttachmentOptions { AllowedExtensions = ".pdf,.mp4" };
        Assert.Throws<ArgumentException>(() => AttachmentValidator.Validate("clip.mp4", 100, new byte[100], options));
        Assert.DoesNotContain(".svg", AttachmentValidator.SupportedExtensions);
        Assert.DoesNotContain(".svg", new AttachmentOptions().GetAllowedExtensionSet());
    }

    // ------------------------------------------------------------------------------------------ success & serving

    [Fact]
    public async Task AValidFile_IsStored_RecordedWithAServerDecidedType_AndServedBackIntact()
    {
        using var db = Db();
        var (c, u) = await SeedCaseAsync(db);
        var svc = Service(db);

        // The client lies about the type; the server decides from the verified content/extension.
        var dto = await svc.UploadAttachmentAsync(c.Id, File("../../etc/Statement.PDF", PdfBytes, "text/html"), "  bank statement  ", u.Id);

        Assert.Equal("Statement.PDF", dto.FileName);          // no path
        Assert.Equal("application/pdf", dto.FileType);        // not text/html
        Assert.Equal(PdfBytes.Length, dto.FileSizeBytes);

        var row = await db.CaseAttachments.AsNoTracking().SingleAsync();
        Assert.Equal("bank statement", row.Note);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(PdfBytes)), row.ContentHash);
        Assert.EndsWith(".pdf", row.StoragePath);
        Assert.DoesNotContain("/", row.StoragePath);
        Assert.Contains(await db.CaseEvents.ToListAsync(), e => e.CaseId == c.Id && e.Message.Contains("Attached file: Statement.PDF"));

        await using var download = (await svc.OpenDownloadAsync(c.Id, dto.Id)).Content;
        using var ms = new MemoryStream();
        await download.CopyToAsync(ms);
        Assert.Equal(PdfBytes, ms.ToArray());

        Assert.Single(await svc.GetAttachmentsAsync(c.Id));
    }

    [Fact]
    public async Task AnAttachmentCannotBeDownloadedThroughAnotherCasesUrl()
    {
        using var db = Db();
        var (caseA, u) = await SeedCaseAsync(db);
        var svc = Service(db);
        var dto = await svc.UploadAttachmentAsync(caseA.Id, File("a.png", PngBytes, "image/png"), null, u.Id);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.OpenDownloadAsync(Guid.NewGuid(), dto.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.GetAttachmentsAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.UploadAttachmentAsync(Guid.NewGuid(), File("a.png", PngBytes, "image/png"), null, u.Id));
    }

    [Fact]
    public async Task TheStoreRefusesPathTraversalKeys()
    {
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(_dir);
        env.Setup(e => e.EnvironmentName).Returns("Development");
        var store = new LocalDiskAttachmentStore(Options.Create(new AttachmentOptions { StoragePath = _dir }), env.Object, NullLogger<LocalDiskAttachmentStore>.Instance);

        foreach (var key in new[] { "../secret.pdf", "a/b.pdf", "..", "" })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.OpenReadAsync(key));
    }

    [Fact]
    public async Task AMissingStoredFile_IsReportedClearly_NotAsAServerCrash()
    {
        using var db = Db();
        var (c, u) = await SeedCaseAsync(db);
        var svc = Service(db);
        var dto = await svc.UploadAttachmentAsync(c.Id, File("a.pdf", PdfBytes), null, u.Id);
        foreach (var f in Directory.GetFiles(_dir)) System.IO.File.Delete(f);

        await Assert.ThrowsAsync<FileNotFoundException>(() => svc.OpenDownloadAsync(c.Id, dto.Id));
    }
}
