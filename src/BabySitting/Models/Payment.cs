using System.ComponentModel.DataAnnotations;

namespace BabySitting.Models;

public class Payment : Entity
{
    public Guid BookingId { get; set; }
    public BabysittingBooking Booking { get; set; } = null!;
    public Guid PayerId { get; set; }
    public ApplicationUser Payer { get; set; } = null!;
    [Range(typeof(decimal), "0.01", "99999999")] public decimal Amount { get; set; }
    [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = "USD";
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    [Required, MaxLength(30)] public string Provider { get; set; } = "Dummy";
    [MaxLength(200)] public string? ProviderTransactionId { get; set; }
    public Guid IdempotencyKey { get; set; } = Guid.NewGuid();
    public DateTimeOffset? PaidAtUtc { get; set; }
    [MaxLength(500)] public string? FailureReason { get; set; }
}
