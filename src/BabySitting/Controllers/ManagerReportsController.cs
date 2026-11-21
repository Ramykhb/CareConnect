using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BabySitting.Models;
using BabySitting.Services.Portal;

namespace BabySitting.Controllers;

// The manager's overview: people, requests, bookings, money and anything waiting for a manager.
[Authorize(Roles = Roles.PlatformManager)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ManagerReportsController(IManagerReportService reports) : Controller
{
    // GET /ManagerReports?days=7|30|90|365
    [HttpGet]
    public async Task<IActionResult> Index(int? days, CancellationToken ct) =>
        View(await reports.ReportAsync(ManagerReportService.Normalize(days), ct));

    // GET /ManagerReports/Export?days=30 downloads the same numbers in CSV, PDF or Excel format.
    [HttpGet]
    public async Task<IActionResult> Export(int? days, CancellationToken ct, string format = "csv")
    {
        if (format is not ("csv" or "pdf" or "xlsx")) return BadRequest("Choose CSV, PDF or Excel.");
        var report = await reports.ReportAsync(ManagerReportService.Normalize(days), ct);
        var name = $"careconnect-babysitting-report-{report.Days}-days-{report.To:yyyy-MM-dd}";
        if (format == "pdf") return File(ManagerReportDownloads.Pdf(report), "application/pdf", name + ".pdf");
        if (format == "xlsx") return File(ManagerReportDownloads.Xlsx(report), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(reports.ToCsv(report))).ToArray();
        return File(bytes, "text/csv", name + ".csv");
    }
}
