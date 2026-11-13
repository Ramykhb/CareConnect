using System.ComponentModel.DataAnnotations;

namespace BabySitting.Models;

public class Review : Entity
{
    public Guid BookingId { get; set; }
    public BabysittingBooking Booking { get; set; } = null!;
    public Guid ReviewerId { get; set; }
    public ApplicationUser Reviewer { get; set; } = null!;
    [MaxLength(2000)] public string? SitterReply { get; set; }
    public DateTimeOffset? RepliedAtUtc { get; set; }
    [Range(1, 5)] public int Rating { get; set; }
    [MaxLength(2000)] public string? Comment { get; set; }
}
