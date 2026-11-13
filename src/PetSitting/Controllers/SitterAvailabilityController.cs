using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PetSitting.Models;
using PetSitting.Services.Scheduling;
using PetSitting.ViewModels;

namespace PetSitting.Controllers;

[Authorize(Roles = Roles.Sitter)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SitterAvailabilityController(ISitterAvailabilityService availability, UserManager<ApplicationUser> users) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(DateOnly? date, string? view, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User); return account is null ? Challenge() : View(await availability.LoadAsync(account, date, view, ct));
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([Bind(Prefix = "Editor")] AvailabilityEditForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            var account = await users.GetUserAsync(User); if (account is null) return Challenge();
            var model = await availability.LoadAsync(account, form.StartDate, form.View, ct); model.Editor = form;
            return View("Index", model);
        }
        var result = await availability.SaveAsync(Guid.Parse(users.GetUserId(User)!), form, ct);
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Index), new { date = form.StartDate.ToString("yyyy-MM-dd"), view = form.View == "week" ? "week" : "month" });
    }
    [HttpGet]
    public async Task<IActionResult> Preview([Bind(Prefix = "Editor")] AvailabilityEditForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest("Choose a valid date range.");
        try { return Json(new { affected = await availability.AffectedAsync(Guid.Parse(users.GetUserId(User)!), form, ct) }); }
        catch (ArgumentException) { return BadRequest("Choose valid local times."); }
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(bool accepting, string version, CancellationToken ct)
    {
        var result = await availability.ToggleAsync(Guid.Parse(users.GetUserId(User)!), accepting, version, ct);
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Index));
    }
}
