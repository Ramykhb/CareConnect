using System.ComponentModel.DataAnnotations;

namespace BabySitting.Models;

public class SitterAvailability : Entity
{
    public Guid SitterProfileId { get; set; }
    public BabysitterProfile SitterProfile { get; set; } = null!;
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset EndsAtUtc { get; set; }
}
