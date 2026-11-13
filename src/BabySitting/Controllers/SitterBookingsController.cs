using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using BabySitting.Models;
using BabySitting.Services.Bookings;
using BabySitting.ViewModels;

namespace BabySitting.Controllers;

[Authorize(Roles = Roles.Sitter)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SitterBookingsController(ISitterBookingService bookings, UserManager<ApplicationUser> users) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? status, string? search, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User); if (account is null) return Challenge();
        var model = await bookings.LoadAsync(account, ct: ct);
        model.Filter = SitterBookingsViewModel.Normalize(status); model.Search = search is null ? null : search.Trim()[..Math.Min(search.Trim().Length, 150)];
        return View(model);
    }
    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User); if (account is null) return Challenge();
        var model = await bookings.LoadAsync(account, id, ct); return model.Booking is null ? NotFound() : View(model);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Transition(BookingTransitionForm form, CancellationToken ct) => await Result(form.Id, () => bookings.TransitionAsync(Guid.Parse(users.GetUserId(User)!), form, ct));
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Task(CareTaskForm form, CancellationToken ct) => await Result(form.Id, () => bookings.TaskAsync(Guid.Parse(users.GetUserId(User)!), form, ct));
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTask(AddCareTaskForm form, CancellationToken ct) => await Result(form.Id, () => bookings.AddTaskAsync(Guid.Parse(users.GetUserId(User)!), form, ct));
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(6291456)]
    public async Task<IActionResult> SendUpdate(BookingUpdateForm form, CancellationToken ct) => await Result(form.Id, () => bookings.UpdateAsync(Guid.Parse(users.GetUserId(User)!), form, false, ct));
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReportIssue(BookingUpdateForm form, CancellationToken ct) => await Result(form.Id, () => bookings.UpdateAsync(Guid.Parse(users.GetUserId(User)!), form, true, ct));
    [HttpGet]
    public async Task<IActionResult> Export(string? status, string? search, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User); if (account is null) return Challenge();
        var model = await bookings.LoadAsync(account, ct: ct); model.Filter = SitterBookingsViewModel.Normalize(status); model.Search = search;
        var text = new StringBuilder("Booking,Service,Children,Parent,Starts (Beirut),Ends (Beirut),Status,Amount,Currency\r\n");
        foreach (var b in model.Visible) text.AppendLine(string.Join(",", new[] { SitterBookingsViewModel.Reference(b), DashboardViewModel.Service(b.Request.ServiceType), SitterBookingsViewModel.ChildrenSummary(b), b.Customer.FirstName + " " + b.Customer.LastName, DashboardViewModel.Local(b.StartsAtUtc).ToString("yyyy-MM-dd HH:mm"), DashboardViewModel.Local(b.EndsAtUtc).ToString("yyyy-MM-dd HH:mm"), BookingRules.Label(b), b.TotalAmount.ToString(CultureInfo.InvariantCulture), b.Currency }.Select(Csv)));
        return File(Encoding.UTF8.GetBytes(text.ToString()), "text/csv; charset=utf-8", "careconnect-bookings.csv");
    }
    private async Task<IActionResult> Result(Guid id, Func<Task<BookingWriteResult>> operation)
    {
        if (!ModelState.IsValid) { TempData["Error"] = ModelState.Values.SelectMany(v => v.Errors).First().ErrorMessage; return RedirectToAction(nameof(Details), new { id }); }
        var result = await operation(); if (result.NotFound) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Details), new { id });
    }
    // Quoting alone does not stop spreadsheet formulas in user-provided fields.
    private static string Csv(string value) => "\"" + ((value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' or '\t' or '\r') ? "'" : "") + value.Replace("\"", "\"\"") + "\"";
}
