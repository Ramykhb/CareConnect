using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

public class Review : Entity
{
    public Guid BookingId { get; set; }
    public PetBooking Booking { get; set; } = null!;
    public Guid ReviewerId { get; set; }
    public ApplicationUser Reviewer { get; set; } = null!;
    [Range(1, 5)] public int Rating { get; set; }
    [MaxLength(2000)] public string? Comment { get; set; }
}
