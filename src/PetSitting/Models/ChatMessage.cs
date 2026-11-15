using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

// One chat message in a booking's conversation between the pet owner and the sitter.
// It is deleted automatically 24 hours after the recipient reads it.
public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookingId { get; set; }
    public PetBooking Booking { get; set; } = null!;
    public Guid SenderId { get; set; }
    public Guid RecipientId { get; set; }
    [Required, MaxLength(2000)] public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAtUtc { get; set; }
}
