using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BabySitting.Services.Notifications;

// The private live channel (same idea as WatchWise's `notifications.{id}` Reverb channel).
// SignalR addresses each signed-in user by their user id, so a user only receives their own events.
[Authorize]
public sealed class NotificationHub : Hub
{
    public const string Path = "/hubs/notifications";
    public const string Pushed = "notification.pushed";     // a new notification: show it, and pop it up unless silent
    public const string Changed = "notifications.changed";  // read/unread/dismissed: refresh the unread count
}
