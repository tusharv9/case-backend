using CaseManagement.Api.Services;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>Which attachments are shown in the case instead of downloaded.</summary>
/// <remarks>
/// A previewed file is rendered by the agent's browser. Pictures and PDFs are safe to show that way; an SVG or HTML file can carry
/// script, so it must never be offered for preview, however it is named or typed by whoever sent it. The type comes from the
/// extension the upload checks verified, never from the sender's claimed content type.
/// </remarks>
public class AttachmentPreviewTests
{
    [Theory]
    [InlineData("scan.PDF", "application/pdf")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("chart.png", "image/png")]
    [InlineData("anim.gif", "image/gif")]
    public void Pictures_and_PDFs_are_previewed_as_their_type(string name, string type) =>
        Assert.Equal(type, AttachmentService.PreviewTypeOf(name));

    [Theory]
    [InlineData("logo.svg")]
    [InlineData("page.html")]
    [InlineData("sheet.xlsx")]
    [InlineData("archive.zip")]
    [InlineData("no-extension")]
    public void Everything_else_is_download_only(string name) =>
        Assert.Null(AttachmentService.PreviewTypeOf(name));
}
