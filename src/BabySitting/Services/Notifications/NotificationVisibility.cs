using BabySitting.Data;
using BabySitting.Models;

namespace BabySitting.Services.Notifications;

// Keep the activity records, but apply one preference policy to every notification surface.
public static class NotificationVisibility
{
    public static IQueryable<Notification> ForUser(BabySittingDbContext db, Guid userId) =>
        db.Notifications.Where(n => n.UserId == userId && !db.AlertPreferences.Any(p => p.UserId == userId &&
            ((n.Category == NotificationCategory.Request && !p.Requests)
            || (n.Category == NotificationCategory.Booking && !p.Bookings)
            || (n.Category == NotificationCategory.Payment && !p.Payments)
            || (n.Category == NotificationCategory.Review && !p.Reviews))));
}
