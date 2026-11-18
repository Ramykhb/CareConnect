using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;

namespace PetSitting.Services.Notifications;

public sealed record LiveNotification(Guid Id, string Title, string Body, string? Url, string Category, DateTimeOffset CreatedAtUtc, bool Read)
{
    public static LiveNotification From(Notification n) => new(n.Id, n.Title, n.Body, n.RelativeUrl, n.Category.ToString().ToLowerInvariant(), n.CreatedAtUtc, n.ReadAtUtc is not null);
}

// Pushes events to a user's open tabs (WatchWise: LiveNotifications::push / ::changed).
// A failed push never breaks the request that saved the notification; the page can always reload the feed.
public sealed class LiveNotifications(IHubContext<NotificationHub> hub, ILogger<LiveNotifications> logger, IServiceScopeFactory scopes, NotificationEmailQueue? emails = null)
{
    public async Task PushAsync(Notification notification, bool silent = false)
    {
        // Already-read records (history backfills) are never emailed.
        if (!silent) emails?.Enqueue(notification.Id);
        try
        {
            // Use an independent scope after commit; the writer may still hold its transaction object.
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
            var visible = await NotificationVisibility.ForUser(db, notification.UserId).AsNoTracking().SingleOrDefaultAsync(n => n.Id == notification.Id);
            if (visible is not null)
                await SendAsync(visible.UserId, NotificationHub.Pushed, new { notification = LiveNotification.From(visible), silent });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Live notification could not be loaded for delivery."); }
    }

    public Task ChangedAsync(Guid userId, bool preferencesChanged = false) => SendAsync(userId, NotificationHub.Changed, new { userId, preferencesChanged });

    private async Task SendAsync(Guid userId, string eventName, object payload)
    {
        try { await hub.Clients.User(userId.ToString()).SendAsync(eventName, payload); }
        catch (Exception ex) { logger.LogWarning(ex, "Live notification {Event} could not be sent.", eventName); }
    }
}
