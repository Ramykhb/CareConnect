using System.ComponentModel.DataAnnotations;
namespace BabySitting.Models;

public sealed class BookingCareTask : Entity
{
    public Guid BookingId { get; set; }
    public BabysittingBooking Booking { get; set; } = null!;
    [Required, MaxLength(80)] public string Key { get; set; } = "";
    [Required, MaxLength(200)] public string Label { get; set; } = "";
    public DateTimeOffset? DueAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    // The care note this item came from, copied when the checklist was saved (null for items the sitter added).
    [MaxLength(2000)] public string? Note { get; set; }
}
