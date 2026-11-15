using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using BabySitting.Models;
using BabySitting.Services.Chat;

namespace BabySitting.Controllers;

// Messages: one conversation per booking. Sending normally goes over the socket (ChatHub);
// the form POST below is the fallback when JavaScript or the socket is unavailable.
[Authorize(Roles = Roles.Customer + "," + Roles.Sitter)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MessagesController(IChatService chat, IHubContext<ChatHub> hub, UserManager<ApplicationUser> userManager) : Controller
{
    private Guid UserId => Guid.Parse(userManager.GetUserId(User)!);

    [HttpGet]
    public async Task<IActionResult> Index() => View(await chat.ConversationsAsync(UserId));

    [HttpGet]
    public async Task<IActionResult> Chat(Guid id)
    {
        var thread = await chat.ThreadAsync(UserId, id);
        if (thread is null) return NotFound();
        var read = await chat.MarkReadAsync(UserId, id);
        if (read.Count > 0) await hub.Clients.Group(ChatHub.Group(id)).SendAsync(ChatHub.Read, read);
        return View(thread with { Messages = thread.Messages.Select(m => read.FirstOrDefault(r => r.Id == m.Id) ?? m).ToList() });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(Guid id, string? body)
    {
        if (!await chat.CanAccessAsync(UserId, id)) return NotFound();
        var message = await chat.SendAsync(UserId, id, body);
        if (message is null) TempData["Error"] = "Write a message of up to 2,000 characters.";
        else await hub.Clients.Group(ChatHub.Group(id)).SendAsync(ChatHub.Received, message);
        return RedirectToAction(nameof(Chat), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Unread() => Json(new { unread = await chat.UnreadAsync(UserId) });
}
