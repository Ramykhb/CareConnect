namespace BabySitting.Models;

public enum NotificationCategory { General, Request, Booking, Payment, Review }
public sealed class AlertPreferences : Entity
{
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public bool Requests { get; set; } = true;
    public bool Bookings { get; set; } = true;
    public bool Payments { get; set; } = true;
    public bool Reviews { get; set; } = true;
    public bool RequestSound { get; set; }
    // Email copies are opt-in; in-app alerts stay on by default.
    public bool EmailRequests { get; set; }
    public bool EmailBookings { get; set; }
    public bool EmailPayments { get; set; }
    public bool EmailReviews { get; set; }
    public bool EmailAllows(NotificationCategory category) => category switch { NotificationCategory.Request => EmailRequests, NotificationCategory.Booking => EmailBookings, NotificationCategory.Payment => EmailPayments, NotificationCategory.Review => EmailReviews, _ => false };
    public bool Allows(NotificationCategory category) => category switch { NotificationCategory.Request => Requests, NotificationCategory.Booking => Bookings, NotificationCategory.Payment => Payments, NotificationCategory.Review => Reviews, _ => true };
}
