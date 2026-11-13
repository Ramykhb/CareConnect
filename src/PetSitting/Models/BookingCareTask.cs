using System.ComponentModel.DataAnnotations;
namespace PetSitting.Models;

public sealed class BookingCareTask : Entity
{
    public Guid BookingId { get; set; }
    public PetBooking Booking { get; set; } = null!;
    [Required, MaxLength(80)] public string Key { get; set; } = "";
    [Required, MaxLength(200)] public string Label { get; set; } = "";
    public DateTimeOffset? DueAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
