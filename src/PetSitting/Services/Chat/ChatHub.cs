using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace PetSitting.Services.Chat;

// The chat socket. A connection joins a booking's group only after membership is checked,
// so messages and read receipts reach only the pet owner and the sitter of that booking.
[Authorize]
public sealed class ChatHub(IChatService chat) : Hub
{
    public const string Path = "/hubs/chat";
    public const string Received = "message.received";
    public const string Read = "messages.read";
    public static string Group(Guid bookingId) => "booking:" + bookingId.ToString("N");
    private Guid UserId => Guid.Parse(Context.UserIdentifier!);

    public async Task Join(Guid bookingId)
    {
        if (!await chat.CanAccessAsync(UserId, bookingId)) throw new HubException("You can't open this conversation.");
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(bookingId));
    }

    public async Task Send(Guid bookingId, string body)
    {
        var message = await chat.SendAsync(UserId, bookingId, body) ?? throw new HubException("Write a message of up to 2,000 characters.");
        await Clients.Group(Group(bookingId)).SendAsync(Received, message);
    }

    public async Task MarkRead(Guid bookingId)
    {
        var read = await chat.MarkReadAsync(UserId, bookingId);
        if (read.Count > 0) await Clients.Group(Group(bookingId)).SendAsync(Read, read);
    }
}
