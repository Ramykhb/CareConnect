using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BabySitting.Services.Chat;

// The chat socket. A connection joins a booking's group only after membership is checked,
// so messages and read receipts reach only the parent and the sitter of that booking.
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

    // Join before reading history so concurrent messages also arrive over the socket.
    public async Task<IReadOnlyList<ChatMessageDto>> Sync(Guid bookingId)
    {
        await Join(bookingId);
        var thread = await chat.ThreadAsync(UserId, bookingId);
        return thread?.Messages ?? throw new HubException("You can't open this conversation.");
    }

    public async Task MarkRead(Guid bookingId, Guid[] messageIds)
    {
        if (messageIds is null || messageIds.Length > 200) throw new HubException("Send up to 200 displayed message IDs.");
        var read = await chat.MarkReadAsync(UserId, bookingId, messageIds: messageIds);
        if (read.Count > 0) await Clients.Group(Group(bookingId)).SendAsync(Read, read);
    }
}
