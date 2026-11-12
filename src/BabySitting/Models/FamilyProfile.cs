using System.ComponentModel.DataAnnotations;

namespace BabySitting.Models;

public class FamilyProfile : Entity
{
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    [Range(1, 20)] public int NumberOfChildren { get; set; } = 1;
    [Required, MaxLength(150)] public string City { get; set; } = string.Empty;
    // Restricted local care information: do not include child names, birth dates, or schools.
    [MaxLength(2000)] public string? CarePreferences { get; set; }
}
