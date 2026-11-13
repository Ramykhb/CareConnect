using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using BabySitting.Models;
using BabySitting.Services.Portal;
using BabySitting.Services.Scheduling;
using BabySitting.ViewModels;

namespace BabySitting.Controllers;

[Authorize(Roles = Roles.Sitter)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("Sitter")]
public sealed class SitterPortalController(ISitterReportingService reports, ISitterAccountService accountService, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, TimeProvider clock) : Controller
{
    private Guid UserId => Guid.Parse(users.GetUserId(User)!);
    [HttpGet("Earnings")]
    public async Task<IActionResult> Earnings(int? year, string? currency, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User); if (account is null) return Challenge();
        var today = ScheduleMath.LocalDate(clock.GetUtcNow()); var selected = year is >= 2000 && year <= today.Year ? year.Value : today.Year;
        if (!ValidCurrency(currency)) return BadRequest("Choose a three-letter currency code.");
        return View(await reports.LoadAsync(account, new(selected, 1, 1), selected == today.Year ? today : new(selected, 12, 31), currency, ct));
    }
    [HttpGet("Analytics")]
    public async Task<IActionResult> Analytics(DateOnly? from, DateOnly? to, string? currency, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User); if (account is null) return Challenge();
        var range = Range(from, to); if (range is null || !ValidCurrency(currency)) return BadRequest("Choose a reporting period of up to one year ending no later than today, and a three-letter currency.");
        return View(await reports.LoadAsync(account, range.Value.From, range.Value.To, currency, ct));
    }
    [HttpGet("Analytics/Export")]
    public async Task<IActionResult> Export(DateOnly? from, DateOnly? to, string? currency, string format, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User); if (account is null) return Challenge();
        var range = Range(from, to); if (range is null || !ValidCurrency(currency) || format is not ("pdf" or "xlsx")) return BadRequest("Choose a supported report period and format.");
        var model = await reports.LoadAsync(account, range.Value.From, range.Value.To, currency, ct);
        return format == "pdf" ? File(ReportDownloads.Pdf(model), "application/pdf", "careconnect-monthly-report.pdf") : File(ReportDownloads.Xlsx(model), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "careconnect-monthly-report.xlsx");
    }
    [HttpGet("Earnings/Export")]
    public async Task<IActionResult> EarningsCsv(int? year, string? currency, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User); if (account is null) return Challenge();
        var today = ScheduleMath.LocalDate(clock.GetUtcNow()); var selected = year is >= 2000 && year <= today.Year ? year.Value : today.Year;
        if (!ValidCurrency(currency)) return BadRequest("Choose a supported currency code.");
        var model = await reports.LoadAsync(account, new(selected, 1, 1), selected == today.Year ? today : new(selected, 12, 31), currency, ct);
        var text = new StringBuilder("Parent,Booking,Service,Date (Beirut),Payment status,Amount,Currency\r\n");
        foreach (var p in model.PeriodPayments.OrderByDescending(p => p.PaidAtUtc ?? p.CreatedAtUtc)) text.AppendLine(string.Join(",", new[] { p.Booking.Customer.FirstName + " " + p.Booking.Customer.LastName, SitterBookingsViewModel.Reference(p.Booking), DashboardViewModel.Service(p.Booking.Request.ServiceType), DashboardViewModel.Local(p.PaidAtUtc ?? p.CreatedAtUtc).ToString("yyyy-MM-dd HH:mm"), p.Status.ToString(), p.Amount.ToString(CultureInfo.InvariantCulture), p.Currency }.Select(Csv)));
        return File(Encoding.UTF8.GetBytes(text.ToString()), "text/csv; charset=utf-8", "careconnect-payments.csv");
    }
    [HttpGet("Reviews")]
    public async Task<IActionResult> Reviews(int? stars, string? search, string? sort, bool unanswered, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User); if (account is null) return Challenge();
        var model = await accountService.ReviewsAsync(account, ct); model.Stars = stars is >= 1 and <= 5 ? stars : null; model.Search = Search(search); model.Sort = sort is "highest" or "lowest" ? sort : "recent"; model.Unanswered = unanswered;
        return View(model);
    }
    [HttpPost("Reviews/Reply"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(ReviewReplyForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "Enter a reply of up to 2,000 characters."; return RedirectToAction(nameof(Reviews)); }
        var result = await accountService.ReplyAsync(UserId, form, ct); if (result.NotFound) return NotFound(); SetNotice(result);
        return RedirectToAction(nameof(Reviews), null, null, "review-" + form.Id);
    }
    [HttpGet("Notifications")]
    public async Task<IActionResult> Notifications(string? category, string? search, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User); if (account is null) return Challenge();
        var model = await accountService.NotificationsAsync(account, ct); model.Filter = Category(category); model.Search = Search(search); return View(model);
    }
    [HttpGet("Notifications/Feed")]
    public async Task<IActionResult> NotificationsFeed(string? category, string? search, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User); if (account is null) return Challenge();
        var model = await accountService.NotificationsAsync(account, ct); model.Filter = Category(category); model.Search = Search(search); return PartialView("_NotificationFeed", model);
    }
    [HttpPost("Notifications/Read"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Read(Guid id, string? returnUrl, CancellationToken ct)
    {
        var result = await accountService.ReadAsync(UserId, id, ct); if (result.NotFound) return NotFound();
        if (result.Succeeded && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl!);
        SetNotice(result); return RedirectToAction(nameof(Notifications));
    }
    [HttpPost("Notifications/ReadAll"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ReadAll(CancellationToken ct) { SetNotice(await accountService.ReadAsync(UserId, null, ct)); return RedirectToAction(nameof(Notifications)); }
    [HttpPost("Alerts"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAlerts([Bind(Prefix = "Alerts")] AlertPreferencesForm form, bool settings, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest("Choose valid alert preferences.");
        SetNotice(await accountService.SaveAlertsAsync(UserId, form, ct)); return RedirectToAction(settings ? nameof(Settings) : nameof(Notifications));
    }
    [HttpGet("Settings")]
    public async Task<IActionResult> Settings(CancellationToken ct) { var account = await users.GetUserAsync(User); return account is null ? Challenge() : View(await accountService.SettingsAsync(account, ct)); }
    [HttpPost("Settings"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings([Bind(Prefix = "Form")] SitterSettingsForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid) { var user = await users.GetUserAsync(User); if (user is null) return Challenge(); var model = await accountService.SettingsAsync(user, ct); model.Form = form; return View("Settings", model); }
        var result = await accountService.SaveSettingsAsync(UserId, form, ct); SetNotice(result);
        if (result.Succeeded) { var user = await users.GetUserAsync(User); if (user is not null) await signIn.RefreshSignInAsync(user); }
        return RedirectToAction(nameof(Settings));
    }
    [HttpPost("Earnings/Request"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestPayout(PayoutRequestForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "Enter a valid payout amount and currency."; return RedirectToAction(nameof(Earnings)); }
        SetNotice(await reports.RequestPayoutAsync(UserId, form, ct)); return RedirectToAction(nameof(Earnings), new { currency = form.Currency });
    }
    [HttpPost("Earnings/Cancel"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelPayout(Guid id, CancellationToken ct) { var result = await reports.CancelPayoutAsync(UserId, id, ct); if (result.NotFound) return NotFound(); SetNotice(result); return RedirectToAction(nameof(Earnings)); }
    private (DateOnly From, DateOnly To)? Range(DateOnly? from, DateOnly? to)
    {
        var today = ScheduleMath.LocalDate(clock.GetUtcNow()); var end = to ?? today; var start = from ?? new DateOnly(today.Year, today.Month, 1).AddMonths(-5);
        return start.Year >= 2000 && end <= today && start <= end && end.DayNumber - start.DayNumber <= 365 ? (start, end) : null;
    }
    private void SetNotice(PortalWriteResult result) => TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
    private static bool ValidCurrency(string? currency) => currency is null || currency.Length == 3 && currency.All(char.IsAsciiLetterUpper);
    private static string? Search(string? text) => text is null ? null : text.Trim()[..Math.Min(text.Trim().Length, 150)];
    private static string Category(string? category) => category is "request" or "booking" or "payment" or "review" ? category : "all";
    private static string Csv(string value) => "\"" + (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' ? "'" : "") + value.Replace("\"", "\"\"") + "\"";
}
