using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using PetSitting.Models;
using PetSitting.ViewModels;

namespace PetSitting.Services.Receipts;

public static class ReceiptPdf
{
    private static readonly Color Ink = Color.FromRgb(25, 27, 28);
    private static readonly Color Muted = Color.FromRgb(101, 117, 96);
    private static readonly Color Sage = Color.FromRgb(237, 242, 232);
    private static readonly Color Lime = Color.FromRgb(236, 255, 140);

    public static byte[] Create(OwnerBookingDetails model)
    {
        if (!model.HasReceipt) throw new ArgumentException("A receipt requires a successful payment.", nameof(model));
        // Embed the app's fonts so output does not depend on the host's installed fonts.
        lock (typeof(GlobalFontSettings))
            GlobalFontSettings.FontResolver ??= new ReceiptFonts();
        var document = new Document();
        document.Info.Title = "Receipt " + model.Item.Reference + " - PawPal";
        document.Info.Author = "CareConnect";
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = "CareConnect Manrope";
        normal.Font.Size = 10;
        normal.Font.Color = Ink;
        normal.ParagraphFormat.SpaceAfter = 5;
        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(2);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2.1);
        var header = section.AddTable();
        header.AddColumn(Unit.FromCentimeter(8.4));
        header.AddColumn(Unit.FromCentimeter(8.4));
        var head = header.AddRow();
        var brand = head[0].AddParagraph("PawPal");
        brand.Format.Font.Size = 25;
        brand.Format.Font.Bold = true;
        var title = head[1].AddParagraph("Payment receipt");
        title.Format.Font.Size = 16;
        title.Format.Alignment = ParagraphAlignment.Right;
        var reference = head[1].AddParagraph("#" + model.Item.Reference);
        reference.Format.Alignment = ParagraphAlignment.Right;
        reference.Format.Font.Color = Muted;
        var rule = section.AddParagraph();
        rule.Format.Borders.Bottom.Color = Lime;
        rule.Format.Borders.Bottom.Width = 4;
        rule.Format.SpaceAfter = 22;

        var details = section.AddTable();
        details.AddColumn(Unit.FromCentimeter(3.6));
        details.AddColumn(Unit.FromCentimeter(13.2));
        Detail(details, "Care", model.Request.Title);
        Detail(details, "Sitter", model.Item.SitterName);
        Detail(details, "Dates (Beirut)", Local(model.Request.StartsAtUtc) + " - " + Local(model.Request.EndsAtUtc));
        var heading = section.AddParagraph("Successful payment records");
        heading.Format.Font.Size = 13;
        heading.Format.Font.Bold = true;
        heading.Format.SpaceBefore = 24;
        heading.Format.SpaceAfter = 12;
        heading.Format.KeepWithNext = true;
        var table = section.AddTable();
        table.AddColumn(Unit.FromCentimeter(3.5));
        table.AddColumn(Unit.FromCentimeter(9.8));
        table.AddColumn(Unit.FromCentimeter(3.5));
        table.Rows.LeftIndent = 0;
        table.TopPadding = table.BottomPadding = Unit.FromPoint(10);
        table.LeftPadding = table.RightPadding = Unit.FromPoint(8);
        var labels = table.AddRow();
        labels.HeadingFormat = true;
        labels.Shading.Color = Sage;
        labels.Format.Font.Bold = true;
        labels[0].AddParagraph("Date"); labels[1].AddParagraph("Provider / reference"); labels[2].AddParagraph("Amount");
        labels[2].Format.Alignment = ParagraphAlignment.Right;
        var payments = model.Booking!.Payments.Where(p => p.Status == PaymentStatus.Succeeded && p.Currency == model.Booking.Currency)
            .OrderBy(p => p.PaidAtUtc ?? p.CreatedAtUtc).ThenBy(p => p.Id).ToArray();
        foreach (var payment in payments)
        {
            var row = table.AddRow();
            row.Borders.Bottom.Color = Sage;
            row.Borders.Bottom.Width = 0.7;
            row[0].AddParagraph(DashboardViewModel.Local(payment.PaidAtUtc ?? payment.CreatedAtUtc).ToString("d MMM yyyy", CultureInfo.InvariantCulture));
            row[1].AddParagraph(payment.Provider == "Dummy" ? "Demo payment" : payment.Provider);
            if (!string.IsNullOrWhiteSpace(payment.ProviderTransactionId))
            {
                var transaction = row[1].AddParagraph(payment.ProviderTransactionId);
                transaction.Format.Font.Size = 8;
                transaction.Format.Font.Color = Muted;
            }
            row[2].AddParagraph(DashboardViewModel.Money(payment.Amount, payment.Currency));
            row[2].Format.Alignment = ParagraphAlignment.Right;
        }
        var total = section.AddParagraph("Paid " + DashboardViewModel.Money(model.Paid, model.Item.Currency));
        total.Format.Font.Size = 21;
        total.Format.Font.Bold = true;
        total.Format.Alignment = ParagraphAlignment.Right;
        total.Format.SpaceBefore = 20;
        total.Format.SpaceAfter = 22;
        if (payments.Any(p => p.Provider == "Dummy"))
        {
            var demo = section.AddParagraph("This receipt includes demo payment records. No real charge was made for those records.");
            demo.Format.Font.Size = 9;
            demo.Format.Font.Color = Muted;
            demo.Format.SpaceBefore = 8;
            demo.Format.SpaceAfter = 12;
        }
        var footer = section.Footers.Primary.AddParagraph("Booking #" + model.Item.Reference + "  |  PawPal by CareConnect");
        footer.Format.Font.Size = 8;
        footer.Format.Font.Color = Muted;
        footer.Format.Borders.Top.Color = Sage;
        footer.Format.Borders.Top.Width = 1;
        footer.Format.SpaceBefore = 10;
        footer.AddLineBreak();
        footer.AddText("Page "); footer.AddPageField(); footer.AddText(" of "); footer.AddNumPagesField();
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output, closeStream: false);
        return output.ToArray();
    }

    private static string Local(DateTimeOffset time) => DashboardViewModel.Local(time).ToString("d MMM yyyy h:mm tt", CultureInfo.InvariantCulture);
    private static void Detail(Table table, string label, string value)
    {
        var row = table.AddRow();
        row.TopPadding = row.BottomPadding = Unit.FromPoint(6);
        row[0].AddParagraph(label).Format.Font.Color = Muted;
        row[1].AddParagraph(value);
    }

    private sealed class ReceiptFonts : IFontResolver
    {
        // MigraDoc also requests a predefined error font; keep all font requests self-contained.
        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
            => new FontResolverInfo(bold ? "ManropeBold" : "ManropeRegular");
        public byte[] GetFont(string faceName)
        {
            var weight = faceName == "ManropeBold" ? "600" : "400";
            using var stream = typeof(ReceiptPdf).Assembly.GetManifestResourceStream("PetSitting.ReceiptFonts.manrope-" + weight + ".ttf")
                ?? throw new InvalidOperationException("Receipt font is missing from the application assembly.");
            using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
        }
    }
}
