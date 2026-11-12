using System.ComponentModel.DataAnnotations;

namespace BabySitting.ViewModels;

// Only the fields a parent may edit. Binding this view model instead of the FamilyProfile entity
// prevents over-posting (e.g. a malicious form changing UserId).
// Privacy rule: no child names, birth dates or schools are collected.
public class FamilyFormViewModel : IValidatableObject
{
    [Range(1, 20, ErrorMessage = "Number of children must be between 1 and 20.")]
    [Display(Name = "Number of children")]
    public int NumberOfChildren { get; set; } = 1;

    [Range(0, 17, ErrorMessage = "Age must be between 0 and 17 years.")]
    [Display(Name = "Youngest child's age (years)")]
    public int YoungestChildAgeYears { get; set; }

    [Range(0, 17, ErrorMessage = "Age must be between 0 and 17 years.")]
    [Display(Name = "Oldest child's age (years)")]
    public int OldestChildAgeYears { get; set; }

    [Required(ErrorMessage = "Please enter your city.")]
    [StringLength(150)]
    public string City { get; set; } = string.Empty;

    [StringLength(2000)]
    [Display(Name = "Health notes")]
    public string? HealthNotes { get; set; }

    [StringLength(2000)]
    [Display(Name = "Daily routines")]
    public string? Routines { get; set; }

    [StringLength(2000)]
    [Display(Name = "Care preferences")]
    public string? CarePreferences { get; set; }

    // Cross-field rule checked by ModelState.IsValid together with the attributes above.
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OldestChildAgeYears < YoungestChildAgeYears)
            yield return new ValidationResult("The oldest child can't be younger than the youngest child.", [nameof(OldestChildAgeYears)]);
        if (NumberOfChildren == 1 && OldestChildAgeYears != YoungestChildAgeYears)
            yield return new ValidationResult("With one child, the youngest and oldest ages should be the same.", [nameof(OldestChildAgeYears)]);
    }
}
