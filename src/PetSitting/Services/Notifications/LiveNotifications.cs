using Microsoft.AspNetCore.SignalR;
using PetSitting.Models;

namespace PetSitting.Services.Notifications;

public sealed record LiveNotification(Guid Id, string Title, string Body, string? Url, string Category, DateTimeOffset CreatedAtUtc, bool Read)
{
    public static LiveNotification From(Notification n) => new(n.Id, n.Title, n.Body, n.RelativeUrl, n.Category.ToString().ToLowerInvariant(), n.CreatedAtUtc, n.ReadAtUtc is not null);
}

// Pushes events to a user's open tabs (WatchWise: LiveNotifications::push / ::changed).
// A failed push never breaks the request that saved the notification; the page can always reload the feed.
public sealed class LiveNotifications(IHubContext<NotificationHub> hub, ILogger<LiveNotifications> logger)
{
    public Task PushAsync(Notification notification, bool silent = false) =>
        SendAsync(notification.UserId, NotificationHub.Pushed, new { notification = LiveNotification.From(notification), silent });

    public Task ChangedAsync(Guid userId) => SendAsync(userId, NotificationHub.Changed, new { userId });

    private async Task SendAsync(Guid userId, string eventName, object payload)
    {
        try { await hub.Clients.User(userId.ToString()).SendAsync(eventName, payload); }
        catch (Exception ex) { logger.LogWarning(ex, "Live notification {Event} could not be sent.", eventName); }
    }
}
