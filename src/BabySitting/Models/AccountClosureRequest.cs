using System.ComponentModel.DataAnnotations;

namespace BabySitting.Models;

public enum ClosureStatus { Pending, Approved, Rejected, Withdrawn }

// A user's request to close an account that has care records, reviewed by a platform manager.
// Approval deactivates and anonymizes the account while bookings, payments and reviews are kept.
public sealed class AccountClosureRequest : Entity
{
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    [MaxLength(1000)] public string? Reason { get; set; }
    public ClosureStatus Status { get; set; } = ClosureStatus.Pending;
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public Guid? DecidedById { get; set; }
    [MaxLength(1000)] public string? ManagerNote { get; set; }
}
