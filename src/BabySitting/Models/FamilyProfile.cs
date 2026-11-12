using System.ComponentModel.DataAnnotations;

namespace BabySitting.Models;

public class FamilyProfile : Entity
{
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    [Range(1, 20)] public int NumberOfChildren { get; set; } = 1;
    [Required, MaxLength(150)] public string City { get; set; } = string.Empty;
    // Restricted local care information: do not include child names, birth dates, or schools.
    // Age range only (no individual birth dates), shown to sitters before they accept.
    [Range(0, 17)] public int YoungestChildAgeYears { get; set; }
    [Range(0, 17)] public int OldestChildAgeYears { get; set; }
    [MaxLength(2000)] public string? HealthNotes { get; set; }
    [MaxLength(2000)] public string? Routines { get; set; }
    [MaxLength(2000)] public string? CarePreferences { get; set; }
}
