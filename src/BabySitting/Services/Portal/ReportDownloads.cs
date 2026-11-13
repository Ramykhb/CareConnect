using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using BabySitting.ViewModels;

namespace BabySitting.Services.Portal;

// Small, dependency-free snapshot reports. Monetary values are never converted between currencies.
public static class ReportDownloads
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static byte[] Pdf(SitterReportViewModel model)
    {
        var content = new StringBuilder();
        void Rect(double x, double y, double w, double h, string color) => content.AppendLine($"{color} rg {x.ToString(Inv)} {y.ToString(Inv)} {w.ToString(Inv)} {h.ToString(Inv)} re f");
        void Text(double x, double y, string value, double size = 10, bool bold = false, string color = "0.10 0.11 0.11") => content.AppendLine($"BT /{(bold ? "F2" : "F1")} {size.ToString(Inv)} Tf {color} rg 1 0 0 1 {x.ToString(Inv)} {y.ToString(Inv)} Tm ({EscapePdf(value)}) Tj ET");
        Rect(0, 825, 595, 17, "0.92 1 0.53"); Text(42, 787, "CareConnect", 24, true); Text(42, 751, "Analytics & reports", 22); Text(42, 730, $"{model.From:dd MMM yyyy} - {model.To:dd MMM yyyy} | Currency: {model.Currency}", 10, false, "0.42 0.47 0.44");
        var metrics = new[] { ("Responses", model.PeriodOffers.Count().ToString(Inv)), ("Accepted", model.Accepted.ToString(Inv)), ("Completed", model.Completed.ToString(Inv)), ($"Earnings ({model.Currency})", model.Gross.ToString("#,0.00", Inv)) };
        for (var i = 0; i < metrics.Length; i++) { var x = 42 + i * 130; Rect(x, 645, 121, 61, i == 3 ? "0.92 1 0.53" : "0.94 0.96 0.94"); Text(x + 10, 686, metrics[i].Item1, 9); Text(x + 10, 658, metrics[i].Item2, metrics[i].Item2.Length > 12 ? 11 : 19, true); }
        Text(42, 608, "Monthly summary", 15, true);
        var columns = new[] { 50d, 151, 218, 281, 344, 417, 502 };
        var headers = new[] { "Month", "Requests", "Accepted", "Rejected", "Completed", "Earnings", "Rating" };
        Rect(42, 571, 511, 24, "0.91 0.94 0.92");
        for (var i = 0; i < headers.Length; i++) Text(columns[i], 579, headers[i], 8.5, true);
        var y = 546d; var rows = model.Months.ToArray();
        for (var r = 0; r < rows.Length; r++)
        {
            var m = rows[r]; if (r % 2 == 0) Rect(42, y - 8, 511, 26, "0.97 0.98 0.97");
            var values = new[] { m.Month.ToString("MMM yyyy", Inv), m.Requests.ToString(Inv), m.Accepted.ToString(Inv), m.Rejected.ToString(Inv), m.Completed.ToString(Inv), m.Earnings.ToString("#,0.00", Inv), m.Rating?.ToString("0.0", Inv) ?? "-" };
            for (var c = 0; c < values.Length; c++) Text(columns[c], y, values[c], 9);
            y -= 26;
        }
        Rect(42, y - 10, 511, 27, "0.92 1 0.53");
        var totals = new[] { "Total", model.PeriodOffers.Count().ToString(Inv), model.Accepted.ToString(Inv), model.Rejected.ToString(Inv), model.Completed.ToString(Inv), model.Gross.ToString("#,0.00", Inv), model.AverageRating?.ToString("0.0", Inv) ?? "-" };
        for (var c = 0; c < totals.Length; c++) Text(columns[c], y, totals[c], 9, true);
        Text(42, y - 43, "Requests count your recorded responses; accepted/rejected are their current outcomes.", 8, false, "0.42 0.47 0.44");
        Text(42, y - 58, "Earnings are successful payment records in the selected currency. No exchange rates are applied.", 8, false, "0.42 0.47 0.44");
        Text(42, 42, "CareConnect by CareConnect | Generated " + DashboardViewModel.Local(model.Now).ToString("dd MMM yyyy HH:mm", Inv) + " Beirut", 8, false, "0.42 0.47 0.44"); Text(525, 42, "1 / 1", 8);
        var streamBytes = Encoding.ASCII.GetBytes(content.ToString());
        using var output = new MemoryStream(); var offsets = new List<long> { 0 };
        void Write(string value) { var bytes = Encoding.ASCII.GetBytes(value); output.Write(bytes); }
        void Obj(int number, string value) { offsets.Add(output.Position); Write($"{number} 0 obj\n{value}\nendobj\n"); }
        Write("%PDF-1.4\n%CareConnect\n"); Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"); Obj(2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> /Contents 4 0 R >>");
        offsets.Add(output.Position); Write($"4 0 obj\n<< /Length {streamBytes.Length} >>\nstream\n"); output.Write(streamBytes); Write("endstream\nendobj\n");
        Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"); Obj(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        var xref = output.Position; Write("xref\n0 7\n0000000000 65535 f \n"); foreach (var offset in offsets.Skip(1)) Write(offset.ToString("0000000000", Inv) + " 00000 n \n");
        Write($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return output.ToArray();
    }
    private static string EscapePdf(string value) => new string(value.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray()).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    public static byte[] Xlsx(SitterReportViewModel model)
    {
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main", r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships", rel = "http://schemas.openxmlformats.org/package/2006/relationships", types = "http://schemas.openxmlformats.org/package/2006/content-types";
        XElement Cell(string address, object? value, int style = 0)
        {
            var cell = new XElement(s + "c", new XAttribute("r", address), new XAttribute("s", style));
            if (value is null) return cell;
            if (value is string text) { cell.SetAttributeValue("t", "inlineStr"); cell.Add(new XElement(s + "is", new XElement(s + "t", text))); }
            else cell.Add(new XElement(s + "v", Convert.ToString(value, Inv)));
            return cell;
        }
        var data = new XElement(s + "sheetData",
            new XElement(s + "row", new XAttribute("r", 1), new XAttribute("ht", 34), new XAttribute("customHeight", 1), Cell("A1", "CareConnect | Monthly summary", 1)),
            new XElement(s + "row", new XAttribute("r", 2), Cell("A2", $"{model.From:yyyy-MM-dd} to {model.To:yyyy-MM-dd} | Currency: {model.Currency}")),
            new XElement(s + "row", new XAttribute("r", 4), new XAttribute("ht", 25), new XAttribute("customHeight", 1), new[] { "Month", "Requests", "Accepted", "Rejected", "Completed", "Earnings (" + model.Currency + ")", "Avg rating" }.Select((v, i) => Cell(((char)('A' + i)).ToString() + "4", v, 2))));
        var rowNumber = 5;
        foreach (var month in model.Months)
        {
            var values = new object?[] { month.Month.ToDateTime(TimeOnly.MinValue).ToOADate(), month.Requests, month.Accepted, month.Rejected, month.Completed, month.Earnings, month.Rating };
            data.Add(new XElement(s + "row", new XAttribute("r", rowNumber), new XAttribute("ht", 24), new XAttribute("customHeight", 1), values.Select((v, i) => Cell(((char)('A' + i)).ToString() + rowNumber, v, i == 0 ? 3 : i == 5 ? 4 : i == 6 ? 5 : 0)))); rowNumber++;
        }
        var last = rowNumber - 1; rowNumber++;
        var total = new XElement(s + "row", new XAttribute("r", rowNumber), Cell("A" + rowNumber, "Total", 2));
        var totals = new decimal[] { model.PeriodOffers.Count(), model.Accepted, model.Rejected, model.Completed, model.Gross };
        for (var i = 0; i < totals.Length; i++) { var col = ((char)('B' + i)).ToString(); var cell = Cell(col + rowNumber, totals[i], i == 4 ? 4 : 2); cell.AddFirst(new XElement(s + "f", $"SUM({col}5:{col}{last})")); total.Add(cell); }
        total.Add(Cell("G" + rowNumber, model.AverageRating, 5)); data.Add(total);
        data.Add(new XElement(s + "row", new XAttribute("r", rowNumber + 2), Cell("A" + (rowNumber + 2), "Source: Your CareConnect response, booking, payment and review records.")));
        var sheet = new XDocument(new XElement(s + "worksheet", new XElement(s + "sheetViews", new XElement(s + "sheetView", new XAttribute("workbookViewId", 0), new XElement(s + "pane", new XAttribute("ySplit", 4), new XAttribute("topLeftCell", "A5"), new XAttribute("state", "frozen")))), new XElement(s + "cols", Enumerable.Range(1, 7).Select(i => new XElement(s + "col", new XAttribute("min", i), new XAttribute("max", i), new XAttribute("width", i is 1 or 6 ? 23 : 17), new XAttribute("customWidth", 1)))), data, new XElement(s + "autoFilter", new XAttribute("ref", $"A4:G{last}")), new XElement(s + "mergeCells", new XElement(s + "mergeCell", new XAttribute("ref", "A1:G1")), new XElement(s + "mergeCell", new XAttribute("ref", "A2:G2")), new XElement(s + "mergeCell", new XAttribute("ref", $"A{rowNumber + 2}:G{rowNumber + 2}")))));
        var styles = $"""
        <styleSheet xmlns="{s}"><numFmts count="3"><numFmt numFmtId="164" formatCode="mmm yyyy"/><numFmt numFmtId="165" formatCode="#,##0.00"/><numFmt numFmtId="166" formatCode="0.0"/></numFmts><fonts count="2"><font><sz val="11"/><color rgb="FF191B1C"/><name val="Calibri"/></font><font><b/><sz val="18"/><color rgb="FF191B1C"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEAFF87"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="6"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="0" fontId="0" fillId="2" borderId="0" xfId="0" applyFill="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="166" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
        """;
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Part(string path, string xml) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(xml); }
            Part("[Content_Types].xml", new XDocument(new XElement(types + "Types", new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")), new XElement(types + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")), new[] { ("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"), ("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"), ("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml") }.Select(p => new XElement(types + "Override", new XAttribute("PartName", p.Item1), new XAttribute("ContentType", p.Item2))))).ToString());
            Part("_rels/.rels", new XElement(rel + "Relationships", new XElement(rel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", r + "/officeDocument"), new XAttribute("Target", "xl/workbook.xml"))).ToString());
            Part("xl/workbook.xml", new XElement(s + "workbook", new XAttribute(XNamespace.Xmlns + "r", r), new XElement(s + "sheets", new XElement(s + "sheet", new XAttribute("name", "Monthly summary"), new XAttribute("sheetId", 1), new XAttribute(r + "id", "rId1"))), new XElement(s + "calcPr", new XAttribute("fullCalcOnLoad", 1))).ToString());
            Part("xl/_rels/workbook.xml.rels", new XElement(rel + "Relationships", new XElement(rel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", r + "/worksheet"), new XAttribute("Target", "worksheets/sheet1.xml")), new XElement(rel + "Relationship", new XAttribute("Id", "rId2"), new XAttribute("Type", r + "/styles"), new XAttribute("Target", "styles.xml"))).ToString());
            Part("xl/worksheets/sheet1.xml", sheet.ToString()); Part("xl/styles.xml", styles);
        }
        return output.ToArray();
    }
}
