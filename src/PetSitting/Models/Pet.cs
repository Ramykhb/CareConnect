using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

public class Pet : Entity
{
    public Guid OwnerId { get; set; }
    public ApplicationUser Owner { get; set; } = null!;
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(50)] public string Species { get; set; } = string.Empty;
    [MaxLength(100)] public string? Breed { get; set; }
    [Range(0, 100)] public int? AgeYears { get; set; }
    [MaxLength(2000)] public string? MedicalNotes { get; set; }
    [MaxLength(2000)] public string? FeedingInstructions { get; set; }
    // Special requirements (behaviour, routines) shown to sitters before they accept.
    [MaxLength(2000)] public string? CareInstructions { get; set; }
    public ICollection<PetCareRequestPet> CareRequests { get; set; } = new List<PetCareRequestPet>();
}
