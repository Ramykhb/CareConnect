using System.ComponentModel.DataAnnotations;
namespace BabySitting.Models;

public enum PayoutStatus { Requested, Paid, Cancelled }
public sealed class SitterPayout : Entity
{
    public Guid SitterProfileId { get; set; }
    public BabysitterProfile SitterProfile { get; set; } = null!;
    public Guid ClientToken { get; set; }
    public decimal Amount { get; set; }
    [Required, StringLength(3)] public string Currency { get; set; } = "USD";
    public PayoutStatus Status { get; set; }
    public DateTimeOffset? PaidAtUtc { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }
    public ICollection<PayoutAllocation> Allocations { get; set; } = new List<PayoutAllocation>();
}
public sealed class PayoutAllocation : Entity
{
    public Guid PayoutId { get; set; }
    public SitterPayout Payout { get; set; } = null!;
    public Guid PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;
    public decimal Amount { get; set; }
}
