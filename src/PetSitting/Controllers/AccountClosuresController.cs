using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PetSitting.Models;
using PetSitting.Services.Accounts;

namespace PetSitting.Controllers;

// Platform managers review requests to close accounts that have care records.
[Authorize(Roles = Roles.PlatformManager)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountClosuresController(IAccountClosureService closures, UserManager<ApplicationUser> userManager) : Controller
{
    private Guid ManagerId => Guid.Parse(userManager.GetUserId(User)!);

    // GET /AccountClosures?status=all
    [HttpGet]
    public async Task<IActionResult> Index(string? status)
    {
        ClosureStatus? filter = status == "all" ? null : Enum.TryParse<ClosureStatus>(status, true, out var s) ? s : ClosureStatus.Pending;
        ViewBag.Filter = filter?.ToString().ToLowerInvariant() ?? "all";
        return View(await closures.ListAsync(filter));
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        var item = await closures.GetAsync(id);
        return item is null ? NotFound() : View(item);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid id, string? note, string? version) => Finish(id, await closures.ApproveAsync(ManagerId, id, note, version));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid id, string? note, string? version) => Finish(id, await closures.RejectAsync(ManagerId, id, note, version));

    private IActionResult Finish(Guid id, ClosureResult result)
    {
        if (result.NotFound) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Details), new { id });
    }
}
