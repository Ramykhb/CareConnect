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
    public bool Allows(NotificationCategory category) => category switch { NotificationCategory.Request => Requests, NotificationCategory.Booking => Bookings, NotificationCategory.Payment => Payments, NotificationCategory.Review => Reviews, _ => true };
}
