using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;

namespace BabySitting.Services.Notifications;

// The notification feed (WatchWise: NotificationFeedService): list, unread count, read/unread, mark all, dismiss.
// Every method is scoped to the signed-in user, and every change tells that user's open tabs to refresh.
public interface INotificationFeedService
{
    Task<IReadOnlyList<Notification>> FeedAsync(Guid userId, bool unreadOnly, CancellationToken ct = default);
    Task<IReadOnlyList<string>> CategoriesAsync(Guid userId, CancellationToken ct = default);
    Task<int> UnreadAsync(Guid userId, CancellationToken ct = default);
    Task<Notification?> MarkReadAsync(Guid userId, Guid id, bool read, CancellationToken ct = default);
    Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default);
    Task<bool> DismissAsync(Guid userId, Guid id, CancellationToken ct = default);
}

public sealed class NotificationFeedService(BabySittingDbContext db, LiveNotifications live, TimeProvider clock) : INotificationFeedService
{
    public const int FeedLimit = 100;

    public async Task<IReadOnlyList<Notification>> FeedAsync(Guid userId, bool unreadOnly, CancellationToken ct = default) =>
        await NotificationVisibility.ForUser(db, userId).AsNoTracking().Where(n => !unreadOnly || n.ReadAtUtc == null)
            .OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id).Take(FeedLimit).ToListAsync(ct);

    public Task<int> UnreadAsync(Guid userId, CancellationToken ct = default) => NotificationVisibility.ForUser(db, userId).CountAsync(n => n.ReadAtUtc == null, ct);

    public async Task<IReadOnlyList<string>> CategoriesAsync(Guid userId, CancellationToken ct = default)
    {
        var preferences = await db.AlertPreferences.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct) ?? new();
        return Enum.GetValues<NotificationCategory>().Where(preferences.Allows).Select(c => c.ToString().ToLowerInvariant()).ToArray();
    }

    // Writes go straight to the database (never through a possibly stale tracked copy), then announce the change.
    public async Task<Notification?> MarkReadAsync(Guid userId, Guid id, bool read, CancellationToken ct = default)
    {
        var mine = NotificationVisibility.ForUser(db, userId).Where(x => x.Id == id);
        var updated = await mine.Where(x => (x.ReadAtUtc != null) != read)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ReadAtUtc, read ? clock.GetUtcNow() : null), ct);
        if (updated > 0) await live.ChangedAsync(userId);
        return await mine.AsNoTracking().SingleOrDefaultAsync(ct);
    }

    public async Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        var count = await NotificationVisibility.ForUser(db, userId).Where(n => n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, clock.GetUtcNow()), ct);
        if (count > 0) await live.ChangedAsync(userId); // bulk updates bypass SaveChanges
        return count;
    }

    public async Task<bool> DismissAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var removed = await NotificationVisibility.ForUser(db, userId).Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        if (removed > 0) await live.ChangedAsync(userId);
        return removed > 0;
    }
}
