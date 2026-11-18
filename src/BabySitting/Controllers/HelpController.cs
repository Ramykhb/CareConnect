using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.ViewModels;

namespace BabySitting.Controllers;

// Help Center: answers to common questions, and a support form that reaches every platform manager.
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class HelpController(BabySittingDbContext db, UserManager<ApplicationUser> users, TimeProvider clock) : Controller
{
    [HttpGet]
    public IActionResult Index() => View(new SupportRequestForm());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Contact(SupportRequestForm form)
    {
        if (!ModelState.IsValid) return View("Index", form);
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var managers = (await users.GetUsersInRoleAsync(Roles.PlatformManager)).Where(m => m.IsActive).ToList();
        var now = clock.GetUtcNow();
        var title = "Support: " + form.Subject.Trim();
        var body = $"{user.FirstName} {user.LastName} ({user.Email}) wrote: {form.Message.Trim()}";
        foreach (var manager in managers)
            db.Notifications.Add(new Notification { UserId = manager.Id, Category = NotificationCategory.General,
                Title = title[..Math.Min(150, title.Length)], Body = body[..Math.Min(2000, body.Length)], CreatedAtUtc = now });
        await db.SaveChangesAsync();
        TempData["Success"] = managers.Count == 0
            ? "Thanks. No platform manager is set up yet, so please try again later."
            : "Thanks, your message was sent to our support team. We'll reply by email.";
        return RedirectToAction(nameof(Index));
    }
}
