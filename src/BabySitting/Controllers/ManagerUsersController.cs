using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using BabySitting.Models;
using BabySitting.Services.Accounts;

namespace BabySitting.Controllers;

// Platform managers: find users, switch accounts off or on, and add other managers.
[Authorize(Roles = Roles.PlatformManager)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ManagerUsersController(IManagerUserService manage, UserManager<ApplicationUser> userManager) : Controller
{
    private Guid ManagerId => Guid.Parse(userManager.GetUserId(User)!);

    // GET /ManagerUsers?search=&role=&status=&page=
    [HttpGet]
    public async Task<IActionResult> Index(string? search, string? role, string? status, int page = 1, CancellationToken ct = default) =>
        View(await manage.ListAsync(search, role, status, page, ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(Guid id, bool active, string? version, string? returnUrl, CancellationToken ct)
    {
        var result = await manage.SetActiveAsync(ManagerId, id, active, version, ct);
        if (result.NotFound) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public IActionResult AddManager() => View(new AddManagerForm());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddManager(AddManagerForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(form);
        var result = await manage.AddManagerAsync(form, ct);
        if (!result.Succeeded) { ModelState.AddModelError("", result.Message); return View(form); }
        TempData["Success"] = result.Message;
        return RedirectToAction(nameof(Index), new { role = Roles.PlatformManager });
    }
}
