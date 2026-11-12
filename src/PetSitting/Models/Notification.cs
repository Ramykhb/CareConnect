using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

public class Notification : Entity
{
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    [Required, MaxLength(150)] public string Title { get; set; } = string.Empty;
    [Required, MaxLength(2000)] public string Body { get; set; } = string.Empty;
    [MaxLength(500)] public string? RelativeUrl { get; set; }
    public DateTimeOffset? ReadAtUtc { get; set; }
}
