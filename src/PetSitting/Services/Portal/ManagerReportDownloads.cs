using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PetSitting.Services.Receipts;

namespace PetSitting.Services.Portal;

public static class ManagerReportDownloads
{
    private sealed record ReportSection(string Title, string[] Headers, IReadOnlyList<object?[]> Rows);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static IReadOnlyList<ReportSection> Sections(ManagerReport r) => [
        new("Summary", ["Metric", "Value"], [
            ["Customers", r.Customers], ["Sitters", r.Sitters], ["Managers", r.Managers],
            ["Switched-off accounts", r.SwitchedOff], ["New accounts", r.NewAccounts],
            ["New requests", r.NewRequests], ["New bookings", r.NewBookings],
            ["Completed bookings", r.CompletedBookings], ["Cancelled bookings", r.CancelledBookings],
            ["Completion rate (%)", r.CompletionRate], ["Cancellation rate (%)", r.CancellationRate],
            ["Money received (USD)", r.Revenue], ["Refunded (USD)", r.Refunded], ["Failed payments", r.PaymentsFailed],
            ["Payouts sent (USD)", r.PayoutsSent], ["Payouts waiting (USD)", r.PayoutsWaiting], ["Pending payout requests", r.PayoutsWaitingCount],
            ["Reviews", r.Reviews], ["Average rating", r.AverageRating],
            ["Account closures waiting", r.ClosuresWaiting], ["Verifications waiting", r.VerificationsWaiting],
            ["Messages waiting", r.MessagesWaiting], ["Messages retrying", r.MessagesRetrying], ["Combined-care links", r.CombinedLinks]
        ]),
        new("Requests by status", ["Status", "Count"], r.RequestsByStatus.Select(s => new object?[] { s.Label, s.Count }).ToArray()),
        new("Bookings by status", ["Status", "Count"], r.BookingsByStatus.Select(s => new object?[] { s.Label, s.Count }).ToArray()),
        new("Six-month trend (UTC)", ["Month", "New bookings", "Money received (USD)"], r.Months.Select(m => new object?[] { m.Label, m.Bookings, m.Revenue }).ToArray()),
        new("Top sitters", ["Sitter", "Completed", "Earned (USD)", "Rating"], r.TopSitters.Select(s => new object?[] { s.Name, s.Completed, s.Earned, s.Rating }).ToArray())
    ];

    private static string Period(ManagerReport r) => $"{r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd} (UTC) | Last {r.Days} days";
    private const string MoneyNote = "Money totals include USD records only; no currency conversion is applied. Activity counts include all currencies.";
    private static string Value(object? value) => value switch {
        null => "-", decimal amount => amount.ToString("#,0.00", Inv), double number => number.ToString("0.#", Inv),
        IFormattable formattable => formattable.ToString(null, Inv), _ => value.ToString() ?? "" };

    public static byte[] Pdf(ManagerReport report)
    {
        ReceiptPdf.ConfigureFonts();
        var document = new Document();
        document.Info.Title = "CareConnect PetSitting manager report";
        document.Info.Author = "CareConnect";
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = "CareConnect Manrope";
        normal.Font.Size = 9;
        document.Styles[StyleNames.Heading2]!.ParagraphFormat.KeepWithNext = true;
        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);
        var title = section.AddParagraph("CareConnect PetSitting");
        title.Format.Font.Size = 20; title.Format.Font.Bold = true;
        section.AddParagraph("Manager overview").Format.Font.Size = 15;
        section.AddParagraph(Period(report));
        section.AddParagraph(MoneyNote).Format.Font.Size = 8;
        foreach (var part in Sections(report))
        {
            var heading = section.AddParagraph(part.Title, StyleNames.Heading2);
            heading.Format.SpaceBefore = 12; heading.Format.SpaceAfter = 6;
            var table = section.AddTable();
            var first = part.Headers.Length == 2 ? 11 : part.Headers.Length == 3 ? 7 : 8;
            table.AddColumn(Unit.FromCentimeter(first));
            for (var i = 1; i < part.Headers.Length; i++) table.AddColumn(Unit.FromCentimeter((17.0 - first) / (part.Headers.Length - 1)));
            var header = table.AddRow(); header.HeadingFormat = true;
            header.Shading.Color = Color.FromRgb(236, 255, 140);
            header.Format.Font.Bold = true;
            for (var i = 0; i < part.Headers.Length; i++) header[i].AddParagraph(part.Headers[i]);
            foreach (var values in part.Rows)
            {
                var row = table.AddRow();
                row.TopPadding = row.BottomPadding = Unit.FromPoint(4);
                for (var i = 0; i < values.Length; i++) row[i].AddParagraph(Value(values[i]));
            }
            if (part.Rows.Count == 0) { var row = table.AddRow(); row[0].AddParagraph("No records for this period."); }
        }
        var footer = section.Footers.Primary.AddParagraph("CareConnect | ");
        footer.AddPageField(); footer.AddText(" / "); footer.AddNumPagesField();
        footer.Format.Font.Size = 8;
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output, closeStream: false);
        return output.ToArray();
    }

    public static byte[] Xlsx(ManagerReport report)
    {
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        var data = new XElement(s + "sheetData");
        var merges = new XElement(s + "mergeCells");
        var row = 0;
        void Row(IEnumerable<object?> values, int style = 0, bool merge = false)
        {
            row++;
            var cells = values.Select((value, col) => {
                var cell = new XElement(s + "c", new XAttribute("r", ((char)('A' + col)).ToString() + row), new XAttribute("s", style));
                if (value is null) return cell;
                if (value is string text) { cell.SetAttributeValue("t", "inlineStr"); cell.Add(new XElement(s + "is", new XElement(s + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text))); }
                else cell.Add(new XElement(s + "v", Convert.ToString(value, Inv)));
                return cell;
            });
            data.Add(new XElement(s + "row", new XAttribute("r", row), cells));
            if (merge) merges.Add(new XElement(s + "mergeCell", new XAttribute("ref", $"A{row}:D{row}")));
        }
        Row(["CareConnect PetSitting manager overview"], 1, true);
        Row([Period(report)], 0, true);
        Row([MoneyNote], 0, true);
        foreach (var part in Sections(report))
        {
            Row([]);
            Row([part.Title], 1, true);
            Row(part.Headers, 2);
            foreach (var values in part.Rows) Row(values);
        }
        var sheet = new XElement(s + "worksheet",
            new XElement(s + "sheetViews", new XElement(s + "sheetView", new XAttribute("workbookViewId", 0),
                new XElement(s + "pane", new XAttribute("ySplit", 3), new XAttribute("topLeftCell", "A4"), new XAttribute("state", "frozen")))),
            new XElement(s + "cols", Enumerable.Range(1, 4).Select(i => new XElement(s + "col", new XAttribute("min", i), new XAttribute("max", i),
                new XAttribute("width", i == 1 ? 55 : 26), new XAttribute("customWidth", 1)))), data, merges);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Part(string name, string xml) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(xml); }
            Part("[Content_Types].xml", new XElement(types + "Types",
                new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(types + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new[] { ("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
                    ("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"), ("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml") }
                    .Select(p => new XElement(types + "Override", new XAttribute("PartName", p.Item1), new XAttribute("ContentType", p.Item2)))).ToString());
            Part("_rels/.rels", new XElement(rel + "Relationships", new XElement(rel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", r.NamespaceName + "/officeDocument"), new XAttribute("Target", "xl/workbook.xml"))).ToString());
            Part("xl/workbook.xml", new XElement(s + "workbook", new XAttribute(XNamespace.Xmlns + "r", r), new XElement(s + "sheets",
                new XElement(s + "sheet", new XAttribute("name", "Manager overview"), new XAttribute("sheetId", 1), new XAttribute(r + "id", "rId1")))).ToString());
            Part("xl/_rels/workbook.xml.rels", new XElement(rel + "Relationships",
                new XElement(rel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", r.NamespaceName + "/worksheet"), new XAttribute("Target", "worksheets/sheet1.xml")),
                new XElement(rel + "Relationship", new XAttribute("Id", "rId2"), new XAttribute("Type", r.NamespaceName + "/styles"), new XAttribute("Target", "styles.xml"))).ToString());
            Part("xl/worksheets/sheet1.xml", sheet.ToString());
            Part("xl/styles.xml", $"""
                <styleSheet xmlns="{s}"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="12"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFECFF8C"/></patternFill></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0"/></cellXfs></styleSheet>
                """);
        }
        return output.ToArray();
    }
}
