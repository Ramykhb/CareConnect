using System.IO.Compression;
using System.Net;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using PdfSharp.Pdf.IO;
using BabySitting.Controllers;
using BabySitting.Services.Portal;
using Xunit;

namespace BabySitting.Tests;

public sealed class ManagerReportExportTests
{
    private static ManagerReport Sample() => new(30, new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2030, 1, 31, 0, 0, 0, TimeSpan.Zero),
        3, 2, 1, 0, 4, 5, [new("Open", 5)], 2, [new("Completed", 1)], 1, 0,
        125.5m, 10m, 1, 20m, 0m, 0, 1, 4.5, 0, 0, 0, 0,
        [new("Jan 2030", 2, 125.5m)], [new("=HYPERLINK(\"x\") Élodie", 1, 50m, 5)]);
    private sealed class Reports : IManagerReportService
    {
        public Task<ManagerReport> ReportAsync(int days, CancellationToken ct = default) => Task.FromResult(Sample());
        public string ToCsv(ManagerReport report) => new ManagerReportService(null!, TimeProvider.System).ToCsv(report);
    }

    [Fact]
    public void Pdf_is_readable_and_includes_report_metadata()
    {
        using var document = PdfReader.Open(new MemoryStream(ManagerReportDownloads.Pdf(Sample())), PdfDocumentOpenMode.Import);
        Assert.True(document.PageCount >= 1);
        Assert.Contains("manager report", document.Info.Title);
        Assert.Equal("CareConnect", document.Info.Author);
    }

    [Fact]
    public void Excel_keeps_numbers_numeric_and_user_names_as_literal_text()
    {
        using var zip = new ZipArchive(new MemoryStream(ManagerReportDownloads.Xlsx(Sample())));
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var sheet = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cells = sheet.Descendants(ns + "c").ToList();
        var name = Assert.Single(cells, c => c.Element(ns + "is")?.Value == "=HYPERLINK(\"x\") Élodie");
        Assert.Equal("inlineStr", name.Attribute("t")!.Value);
        Assert.Empty(sheet.Descendants(ns + "f"));
        Assert.Contains(cells, c => c.Element(ns + "v")?.Value == "125.5");
        Assert.Contains(cells, c => c.Element(ns + "is")?.Value == "Money received (USD)");
        Assert.Contains(cells, c => c.Element(ns + "is")?.Value == "Requests by status");
        Assert.Contains(cells, c => c.Element(ns + "is")?.Value == "Six-month trend (UTC)");
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("xl/styles.xml"));
    }

    [Theory]
    [InlineData("csv", "text/csv")]
    [InlineData("pdf", "application/pdf")]
    [InlineData("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task Export_serves_the_requested_format(string format, string contentType)
    {
        var result = Assert.IsType<FileContentResult>(await new ManagerReportsController(new Reports()).Export(30, default, format));
        Assert.Equal(contentType, result.ContentType);
        Assert.EndsWith("." + format, result.FileDownloadName);
        Assert.NotEmpty(result.FileContents);
    }

    [Fact]
    public async Task Unsupported_export_formats_are_rejected() =>
        Assert.IsType<BadRequestObjectResult>(await new ManagerReportsController(new Reports()).Export(30, default, "exe"));

    [Theory]
    [InlineData("pdf")]
    [InlineData("xlsx")]
    public async Task Downloads_require_authentication(string format)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var result = await client.GetAsync("/ManagerReports/Export?format=" + format);
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.Contains("/Identity/Account/Login", result.Headers.Location!.ToString());
    }
}
