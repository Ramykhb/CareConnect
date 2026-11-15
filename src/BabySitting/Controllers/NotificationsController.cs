using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using BabySitting.Models;
using BabySitting.Services.Notifications;

namespace BabySitting.Controllers;

// The parent notification feed. Sitters keep their own page; the unread count serves everyone's bell.
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NotificationsController(INotificationFeedService feed, UserManager<ApplicationUser> userManager) : Controller
{
    private Guid UserId => Guid.Parse(userManager.GetUserId(User)!);

    // GET /Notifications?filter=unread
    [HttpGet, Authorize(Roles = Roles.Customer)]
    public async Task<IActionResult> Index(string? filter)
    {
        var unreadOnly = filter == "unread";
        ViewBag.Filter = unreadOnly ? "unread" : "all";
        ViewBag.Unread = await feed.UnreadAsync(UserId);
        return View(await feed.FeedAsync(UserId, unreadOnly));
    }

    // GET /Notifications/Count — used by the live client after a "notifications.changed" event.
    [HttpGet]
    public async Task<IActionResult> Count() => Json(new { unread = await feed.UnreadAsync(UserId) });

    // POST /Notifications/Open/{id} — marks it read and follows its link.
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Customer)]
    public async Task<IActionResult> Open(Guid id)
    {
        var n = await feed.MarkReadAsync(UserId, id, true);
        if (n is null) return NotFound();
        return Url.IsLocalUrl(n.RelativeUrl) ? LocalRedirect(n.RelativeUrl!) : RedirectToAction(nameof(Index));
    }

    // POST /Notifications/Read/{id}?read=false — mark read or unread.
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Customer)]
    public async Task<IActionResult> Read(Guid id, bool read = true, string? filter = null)
    {
        if (await feed.MarkReadAsync(UserId, id, read) is null) return NotFound();
        return RedirectToAction(nameof(Index), new { filter });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Customer)]
    public async Task<IActionResult> ReadAll(string? filter = null)
    {
        var count = await feed.MarkAllReadAsync(UserId);
        TempData["Success"] = count == 0 ? "You're all caught up." : "All notifications marked as read.";
        return RedirectToAction(nameof(Index), new { filter });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = Roles.Customer)]
    public async Task<IActionResult> Dismiss(Guid id, string? filter = null)
    {
        if (!await feed.DismissAsync(UserId, id)) return NotFound();
        return RedirectToAction(nameof(Index), new { filter });
    }
}
