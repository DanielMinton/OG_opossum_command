using OG_opossum_command.Core.Maid;
using OG_opossum_command.Core.Maid.Extractors;

namespace OG_opossum_command.Core.Tests.Maid;

public sealed class ExtractorTests : IDisposable
{
    private readonly MaidFixture _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public void Docx_text_title_and_company_come_out_without_office()
    {
        var path = _world.WriteBytes(_world.Downloads, "x.docx", DocBuilder.Docx(["Lease agreement between landlord and tenant", "Monthly rent is due"], title: "Flat Lease", company: "Contoso"));
        var extraction = ContentExtractor.Extract(path, new FileInfo(path).Length);
        Assert.Contains("landlord and tenant", extraction.Text, StringComparison.Ordinal);
        Assert.Equal("Flat Lease", extraction.Title);
        Assert.Equal("Contoso", extraction.Company);
        Assert.False(extraction.IsSigned);
    }

    [Fact]
    public void A_signed_docx_is_detected()
    {
        var path = _world.WriteBytes(_world.Downloads, "s.docx", DocBuilder.Docx(["Hello"], signed: true));
        Assert.True(ContentExtractor.Extract(path, new FileInfo(path).Length).IsSigned);
    }

    [Fact]
    public void Xlsx_and_pptx_text_is_read()
    {
        var xlsx = _world.WriteBytes(_world.Downloads, "b.xlsx", DocBuilder.Xlsx(["Budget", "Payroll"], ["Vendor", "Amount"]));
        var pptx = _world.WriteBytes(_world.Downloads, "p.pptx", DocBuilder.Pptx("Gala agenda", "Volunteer thanks"));
        var sheet = ContentExtractor.Extract(xlsx, new FileInfo(xlsx).Length).Text;
        var slides = ContentExtractor.Extract(pptx, new FileInfo(pptx).Length).Text;
        Assert.Contains("Budget", sheet, StringComparison.Ordinal);
        Assert.Contains("Vendor", sheet, StringComparison.Ordinal);
        Assert.Contains("Gala agenda", slides, StringComparison.Ordinal);
        Assert.Contains("Volunteer thanks", slides, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_text_and_title_come_out_with_pdfpig()
    {
        var path = _world.WriteBytes(_world.Downloads, "i.pdf", DocBuilder.Pdf(["Invoice number 1234", "Amount due 40.00"], title: "Fabrikam Invoice"));
        var extraction = ContentExtractor.Extract(path, new FileInfo(path).Length);
        Assert.Contains("Invoice", extraction.Text, StringComparison.Ordinal);
        Assert.Contains("1234", extraction.Text, StringComparison.Ordinal);
        Assert.Equal("Fabrikam Invoice", extraction.Title);
    }

    [Fact]
    public void A_broken_file_is_a_note_never_an_exception()
    {
        var pdf = _world.Write(_world.Downloads, "broken.pdf", "%PDF-1.4 this is not really a pdf");
        var docx = _world.Write(_world.Downloads, "broken.docx", "not a zip");
        Assert.NotNull(ContentExtractor.Extract(pdf, 40).Note);
        Assert.NotNull(ContentExtractor.Extract(docx, 9).Note);
    }

    [Fact]
    public void Saved_web_pages_are_recognised_and_stripped()
    {
        var path = _world.Write(_world.Downloads, "Recipe.html", "<!-- saved from url=(0040)https://example.com/ --><html><head><title>Best Banana Bread</title><script>var x=1;</script></head><body><p>Flour &amp; bananas</p></body></html>");
        var extraction = ContentExtractor.Extract(path, new FileInfo(path).Length);
        Assert.True(extraction.IsSavedWebPage);
        Assert.Equal("Best Banana Bread", extraction.Title);
        Assert.Contains("Flour & bananas", extraction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("var x", extraction.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Jpeg_exif_gives_camera_and_date_taken()
    {
        var info = ImageMetadata.Parse(DocBuilder.Jpeg(4032, 3024, "Contoso", "Cam 9", "2025:07:04 18:30:00"), "holiday.jpg");
        Assert.Equal("jpeg", info.Format);
        Assert.Equal(4032, info.Width);
        Assert.Equal(3024, info.Height);
        Assert.Equal("Contoso", info.CameraMake);
        Assert.Equal("Cam 9", info.CameraModel);
        Assert.Equal(new DateTime(2025, 7, 4, 18, 30, 0), info.Taken!.Value.DateTime);
        Assert.True(info.LooksLikeCameraPhoto);
        Assert.False(info.LooksLikeScreenshot);
    }

    [Theory]
    [InlineData("Screenshot 2026-01-02 101010.png", 800, 600, null)]
    [InlineData("whatever.png", 1920, 1080, null)]
    [InlineData("clip.png", 640, 480, "Greenshot")]
    public void Screenshots_are_told_apart_from_photos(string name, int width, int height, string? software)
    {
        var info = ImageMetadata.Parse(DocBuilder.Png(width, height, software), name);
        Assert.Equal("png", info.Format);
        Assert.Equal(width, info.Width);
        Assert.True(info.LooksLikeScreenshot);
        Assert.False(info.LooksLikeCameraPhoto);
    }

    [Fact]
    public void Garbage_image_bytes_do_not_throw()
    {
        var info = ImageMetadata.Parse(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 0x00 }, "x.jpg");
        Assert.Equal("jpeg", info.Format);
        Assert.Equal(0, info.Width);
    }
}
