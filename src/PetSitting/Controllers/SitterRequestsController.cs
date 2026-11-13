using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PetSitting.Models;
using PetSitting.Services.Requests;
using PetSitting.ViewModels;

namespace PetSitting.Controllers;

[Authorize(Roles = Roles.Sitter)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SitterRequestsController(ISitterRequestService requests, UserManager<ApplicationUser> users) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? status, string? search, Guid? id, string? confirm, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User);
        if (account is null) return Challenge();
        var model = await requests.LoadAsync(account, ct);
        ApplySelection(model, status, search, id);
        if (id is not null && !model.Items.Any(i => i.Id == id)) return NotFound();
        if (confirm is "accept" or "decline" && model.Selected is not null && (confirm == "accept" ? model.Selected.CanAccept : model.Selected.CanDecline)) model.Confirmation = confirm;
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Updates(string? status, string? search, Guid? id, CancellationToken ct)
    {
        var account = await users.GetUserAsync(User);
        if (account is null) return Challenge();
        var model = await requests.LoadAsync(account, ct);
        ApplySelection(model, status, search, id);
        return PartialView("_Refresh", model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(RequestDecisionForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest("The request decision is invalid. Reload the inbox and try again.");
        var account = await users.GetUserAsync(User);
        if (account is null) return Challenge();
        var result = await requests.DecideAsync(account, form, ct);
        if (result.NotFound) return NotFound();
        TempData[result.Succeeded ? "RequestDecision" : "Error"] = result.Message;
        return RedirectToAction(nameof(Index), new { status = result.Filter, id = result.Succeeded ? (Guid?)form.RequestId : null });
    }

    private static void ApplySelection(SitterRequestsViewModel model, string? status, string? search, Guid? id)
    {
        model.Filter = status is null && id is not null ? model.Items.FirstOrDefault(i => i.Id == id)?.Bucket ?? "new" : SitterRequestsViewModel.NormalizeFilter(status);
        var text = search?.Trim();
        model.Search = text is null ? null : text[..Math.Min(text.Length, 150)];
        model.SelectedId = id;
    }
}
