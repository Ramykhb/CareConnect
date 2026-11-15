using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;

namespace BabySitting.Services.Chat;

public sealed record ChatMessageDto(Guid Id, Guid BookingId, Guid SenderId, string Body, DateTimeOffset CreatedAtUtc, DateTimeOffset? ReadAtUtc)
{
    public DateTimeOffset? ExpiresAtUtc => ReadAtUtc?.Add(ChatService.Lifetime);
    public static ChatMessageDto From(ChatMessage m) => new(m.Id, m.BookingId, m.SenderId, m.Body, m.CreatedAtUtc, m.ReadAtUtc);
}
public sealed record ChatConversation(BabysittingBooking Booking, string OtherName, string Title, ChatMessageDto? Last, int Unread);
public sealed record ChatThread(BabysittingBooking Booking, Guid UserId, string OtherName, string Title, string BookingUrl, IReadOnlyList<ChatMessageDto> Messages);

public interface IChatService
{
    Task<bool> CanAccessAsync(Guid userId, Guid bookingId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatConversation>> ConversationsAsync(Guid userId, CancellationToken ct = default);
    Task<ChatThread?> ThreadAsync(Guid userId, Guid bookingId, CancellationToken ct = default);
    Task<ChatMessageDto?> SendAsync(Guid userId, Guid bookingId, string? body, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessageDto>> MarkReadAsync(Guid userId, Guid bookingId, CancellationToken ct = default);
    Task<int> UnreadAsync(Guid userId, CancellationToken ct = default);
}

// A conversation belongs to one booking; only its parent and its sitter can read or write it.
public sealed class ChatService(BabySittingDbContext db, TimeProvider clock) : IChatService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    public const int MaxLength = 2000;

    private IQueryable<BabysittingBooking> Mine(Guid userId) => db.BabysittingBookings.Where(b => b.CustomerId == userId || b.SitterProfile.UserId == userId);
    // Read messages past their 24 hours are hidden even before the cleanup job removes them.
    private IQueryable<ChatMessage> Visible(DateTimeOffset now) { var cutoff = now - Lifetime; return db.ChatMessages.Where(m => m.ReadAtUtc == null || m.ReadAtUtc > cutoff); }

    public Task<bool> CanAccessAsync(Guid userId, Guid bookingId, CancellationToken ct = default) => Mine(userId).AnyAsync(b => b.Id == bookingId, ct);

    public async Task<IReadOnlyList<ChatConversation>> ConversationsAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var bookings = await Mine(userId).AsNoTracking().Include(b => b.Customer).Include(b => b.SitterProfile).ThenInclude(p => p.User).Include(b => b.Request)
            .AsSplitQuery().OrderByDescending(b => b.StartsAtUtc).ToListAsync(ct);
        var ids = bookings.Select(b => b.Id).ToArray();
        var messages = await Visible(now).AsNoTracking().Where(m => ids.Contains(m.BookingId)).ToListAsync(ct);
        return bookings.Select(b =>
        {
            var mine = messages.Where(m => m.BookingId == b.Id).OrderBy(m => m.CreatedAtUtc).ToList();
            return new ChatConversation(b, OtherName(b, userId), b.Request.Title, mine.Count == 0 ? null : ChatMessageDto.From(mine[^1]), mine.Count(m => m.RecipientId == userId && m.ReadAtUtc == null));
        }).OrderByDescending(c => c.Last?.CreatedAtUtc ?? DateTimeOffset.MinValue).ThenByDescending(c => c.Booking.StartsAtUtc).ToList();
    }

    public async Task<ChatThread?> ThreadAsync(Guid userId, Guid bookingId, CancellationToken ct = default)
    {
        var booking = await Mine(userId).AsNoTracking().Include(b => b.Customer).Include(b => b.SitterProfile).ThenInclude(p => p.User).Include(b => b.Request)
            .AsSplitQuery().SingleOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null) return null;
        var messages = await Visible(clock.GetUtcNow()).AsNoTracking().Where(m => m.BookingId == bookingId).OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id).ToListAsync(ct);
        var url = booking.CustomerId == userId ? "C:/Program Files/Git/ParentBookings/Details/" + booking.RequestId : "/SitterBookings/Details/" + booking.Id;
        return new ChatThread(booking, userId, OtherName(booking, userId), booking.Request.Title, url, messages.Select(ChatMessageDto.From).ToList());
    }

    public async Task<ChatMessageDto?> SendAsync(Guid userId, Guid bookingId, string? body, CancellationToken ct = default)
    {
        body = body?.Trim();
        if (string.IsNullOrEmpty(body) || body.Length > MaxLength) return null;
        var booking = await Mine(userId).AsNoTracking().Include(b => b.SitterProfile).SingleOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null) return null;
        var message = new ChatMessage { BookingId = bookingId, SenderId = userId, Body = body, CreatedAtUtc = clock.GetUtcNow(),
            RecipientId = booking.CustomerId == userId ? booking.SitterProfile.UserId : booking.CustomerId };
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync(ct);
        return ChatMessageDto.From(message);
    }

    // Marks the messages sent to this user as read; their 24-hour countdown starts now.
    public async Task<IReadOnlyList<ChatMessageDto>> MarkReadAsync(Guid userId, Guid bookingId, CancellationToken ct = default)
    {
        if (!await CanAccessAsync(userId, bookingId, ct)) return [];
        var now = clock.GetUtcNow();
        var ids = await db.ChatMessages.Where(m => m.BookingId == bookingId && m.RecipientId == userId && m.ReadAtUtc == null).Select(m => m.Id).ToListAsync(ct);
        if (ids.Count == 0) return [];
        await db.ChatMessages.Where(m => ids.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAtUtc, now), ct);
        return await db.ChatMessages.AsNoTracking().Where(m => ids.Contains(m.Id)).Select(m => new ChatMessageDto(m.Id, m.BookingId, m.SenderId, m.Body, m.CreatedAtUtc, m.ReadAtUtc)).ToListAsync(ct);
    }

    public Task<int> UnreadAsync(Guid userId, CancellationToken ct = default) =>
        db.ChatMessages.CountAsync(m => m.RecipientId == userId && m.ReadAtUtc == null, ct);

    // Used by the background cleanup and by tests.
    public static Task<int> DeleteExpiredAsync(BabySittingDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var cutoff = now - Lifetime;
        return db.ChatMessages.Where(m => m.ReadAtUtc != null && m.ReadAtUtc <= cutoff).ExecuteDeleteAsync(ct);
    }

    private static string OtherName(BabysittingBooking b, Guid userId) => b.CustomerId == userId
        ? b.SitterProfile.User.FirstName + " " + b.SitterProfile.User.LastName
        : b.Customer.FirstName + " " + b.Customer.LastName;
}
