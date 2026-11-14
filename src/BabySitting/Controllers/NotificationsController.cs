using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using BabySitting.Models;
using BabySitting.Services.Bookings;

namespace BabySitting.Controllers;

// Parent notifications: list, open, and mark as read. Sitters have their own page under /Sitter/Notifications.
[Authorize(Roles = Roles.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NotificationsController(IParentPaymentService service, UserManager<ApplicationUser> userManager) : Controller
{
    private Guid UserId => Guid.Parse(userManager.GetUserId(User)!);

    [HttpGet]
    public async Task<IActionResult> Index() => View(await service.NotificationsAsync(UserId));

    // Marks one notification as read, then opens its link when it is a local URL.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Read(Guid id, string? returnUrl)
    {
        if (!await service.MarkReadAsync(UserId, id)) return NotFound();
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReadAll()
    {
        await service.MarkReadAsync(UserId, null);
        TempData["Success"] = "All notifications marked as read.";
        return RedirectToAction(nameof(Index));
    }
}
