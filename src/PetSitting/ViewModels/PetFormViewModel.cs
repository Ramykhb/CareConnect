using System.ComponentModel.DataAnnotations;

namespace PetSitting.ViewModels;

// Only the fields an owner may edit. Using a view model instead of binding the Pet entity
// prevents over-posting (e.g. a malicious form changing OwnerId).
public class PetFormViewModel
{
    public static readonly string[] SpeciesOptions = ["Dog", "Cat", "Rabbit", "Bird", "Other"];

    [Required(ErrorMessage = "Please enter your pet's name.")]
    [StringLength(100)]
    [Display(Name = "Pet name")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Please choose a species.")]
    [StringLength(50)]
    public string Species { get; set; } = string.Empty;

    [StringLength(100)]
    public string? Breed { get; set; }

    [Range(0, 100, ErrorMessage = "Age must be between 0 and 100 years.")]
    [Display(Name = "Age (years)")]
    public int? AgeYears { get; set; }

    [StringLength(2000)]
    [Display(Name = "Medical notes")]
    public string? MedicalNotes { get; set; }

    [StringLength(2000)]
    [Display(Name = "Feeding instructions")]
    public string? FeedingInstructions { get; set; }

    [StringLength(2000)]
    [Display(Name = "Special requirements")]
    public string? CareInstructions { get; set; }
}
