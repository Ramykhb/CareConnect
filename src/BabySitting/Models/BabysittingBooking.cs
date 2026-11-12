using System.ComponentModel.DataAnnotations;

namespace BabySitting.Models;

public class BabysittingBooking : Entity
{
    public Guid RequestId { get; set; }
    public ChildcareRequest Request { get; set; } = null!;
    public Guid OfferId { get; set; }
    public BabysittingOffer Offer { get; set; } = null!;
    public Guid CustomerId { get; set; }
    public ApplicationUser Customer { get; set; } = null!;
    public Guid SitterProfileId { get; set; }
    public BabysitterProfile SitterProfile { get; set; } = null!;
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset EndsAtUtc { get; set; }
    [Range(typeof(decimal), "0", "99999999")] public decimal TotalAmount { get; set; }
    [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = "USD";
    public BookingStatus Status { get; set; } = BookingStatus.Pending;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }
    [MaxLength(1000)] public string? CancellationReason { get; set; }
    public Guid? CorrelationId { get; set; }
    public Guid? ExternalBookingId { get; set; }
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public Review? Review { get; set; }
}
