using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;

namespace PetSitting.Services.Notifications;

// The notification feed (WatchWise: NotificationFeedService): list, unread count, read/unread, mark all, dismiss.
// Every method is scoped to the signed-in user, and every change tells that user's open tabs to refresh.
public interface INotificationFeedService
{
    Task<IReadOnlyList<Notification>> FeedAsync(Guid userId, bool unreadOnly, CancellationToken ct = default);
    Task<int> UnreadAsync(Guid userId, CancellationToken ct = default);
    Task<Notification?> MarkReadAsync(Guid userId, Guid id, bool read, CancellationToken ct = default);
    Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default);
    Task<bool> DismissAsync(Guid userId, Guid id, CancellationToken ct = default);
}

public sealed class NotificationFeedService(PetSittingDbContext db, LiveNotifications live, TimeProvider clock) : INotificationFeedService
{
    public const int FeedLimit = 100;

    public async Task<IReadOnlyList<Notification>> FeedAsync(Guid userId, bool unreadOnly, CancellationToken ct = default) =>
        await db.Notifications.AsNoTracking().Where(n => n.UserId == userId && (!unreadOnly || n.ReadAtUtc == null))
            .OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id).Take(FeedLimit).ToListAsync(ct);

    public Task<int> UnreadAsync(Guid userId, CancellationToken ct = default) => db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, ct);

    // Writes go straight to the database (never through a possibly stale tracked copy), then announce the change.
    public async Task<Notification?> MarkReadAsync(Guid userId, Guid id, bool read, CancellationToken ct = default)
    {
        var mine = db.Notifications.Where(x => x.Id == id && x.UserId == userId);
        var updated = await mine.Where(x => (x.ReadAtUtc != null) != read)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ReadAtUtc, read ? clock.GetUtcNow() : null), ct);
        if (updated > 0) await live.ChangedAsync(userId);
        return await mine.AsNoTracking().SingleOrDefaultAsync(ct);
    }

    public async Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        var count = await db.Notifications.Where(n => n.UserId == userId && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, clock.GetUtcNow()), ct);
        if (count > 0) await live.ChangedAsync(userId); // bulk updates bypass SaveChanges
        return count;
    }

    public async Task<bool> DismissAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var removed = await db.Notifications.Where(x => x.Id == id && x.UserId == userId).ExecuteDeleteAsync(ct);
        if (removed > 0) await live.ChangedAsync(userId);
        return removed > 0;
    }
}
