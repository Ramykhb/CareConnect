using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

public class SitterAvailability : Entity
{
    public Guid SitterProfileId { get; set; }
    public PetSitterProfile SitterProfile { get; set; } = null!;
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset EndsAtUtc { get; set; }
}
