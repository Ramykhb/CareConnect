using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PetSitting.Models;
using PetSitting.Services.Portal;

namespace PetSitting.Controllers;

// The manager's overview: people, requests, bookings, money and anything waiting for a manager.
[Authorize(Roles = Roles.PlatformManager)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ManagerReportsController(IManagerReportService reports) : Controller
{
    // GET /ManagerReports?days=7|30|90|365
    [HttpGet]
    public async Task<IActionResult> Index(int? days, CancellationToken ct) =>
        View(await reports.ReportAsync(ManagerReportService.Normalize(days), ct));

    // GET /ManagerReports/Export?days=30 downloads the same numbers as a CSV file.
    [HttpGet]
    public async Task<IActionResult> Export(int? days, CancellationToken ct)
    {
        var report = await reports.ReportAsync(ManagerReportService.Normalize(days), ct);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(reports.ToCsv(report))).ToArray();
        return File(bytes, "text/csv", $"careconnect-petsitting-report-{report.Days}-days-{report.To:yyyy-MM-dd}.csv");
    }
}
