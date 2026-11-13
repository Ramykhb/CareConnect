using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

// Dates and times are in Asia/Beirut; this preserves wall-clock yearly recurrences across DST.
public sealed class SitterScheduleException : Entity
{
    public Guid SitterProfileId { get; set; }
    public PetSitterProfile SitterProfile { get; set; } = null!;
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public bool AllDay { get; set; } = true;
    public bool IsAvailable { get; set; }
    public bool RepeatYearly { get; set; }
    [MaxLength(500)] public string? Reason { get; set; }
}
