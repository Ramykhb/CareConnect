using System.ComponentModel.DataAnnotations;
using BabySitting.Models;

namespace BabySitting.ViewModels;

// Only the fields a parent may set. Binding this view model instead of the ChildcareRequest entity
// prevents over-posting (e.g. a form changing CustomerId, Status or ExternalRequestId).
// Dates are entered in Beirut local time and converted to UTC by the controller.
public class CareRequestFormViewModel : IValidatableObject
{
    public const int MaxDays = 14;

    // Concurrency token of the request being edited (empty when creating).
    public string? Version { get; set; }

    [Required(ErrorMessage = "Please give your request a short title.")]
    [StringLength(150)]
    [Display(Name = "Title")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Please describe the care you need.")]
    [StringLength(4000)]
    [Display(Name = "What do you need?")]
    public string Description { get; set; } = string.Empty;

    [Display(Name = "Service")]
    public ServiceType ServiceType { get; set; } = ServiceType.Babysitting;

    [Required(ErrorMessage = "Choose when care starts.")]
    [Display(Name = "Starts")]
    public DateTime? StartsAtLocal { get; set; }

    [Required(ErrorMessage = "Choose when care ends.")]
    [Display(Name = "Ends")]
    public DateTime? EndsAtLocal { get; set; }

    [Required(ErrorMessage = "Please enter the city.")]
    [StringLength(150)]
    public string City { get; set; } = string.Empty;

    [StringLength(300)]
    [Display(Name = "Address (shared only with the sitter who accepts)")]
    public string? ServiceAddress { get; set; }

    [Range(typeof(decimal), "0", "99999", ErrorMessage = "Budget must be between 0 and 99,999 USD.")]
    [Display(Name = "Budget (USD, total)")]
    public decimal Budget { get; set; }

    [Range(1, 20, ErrorMessage = "Number of children must be between 1 and 20.")]
    [Display(Name = "Number of children")]
    public int NumberOfChildren { get; set; } = 1;

    [Range(0, 17, ErrorMessage = "Age must be between 0 and 17 years.")]
    [Display(Name = "Youngest child's age (years)")]
    public int YoungestChildAgeYears { get; set; }

    [Range(0, 17, ErrorMessage = "Age must be between 0 and 17 years.")]
    [Display(Name = "Oldest child's age (years)")]
    public int OldestChildAgeYears { get; set; }

    [StringLength(2000)]
    [Display(Name = "Care instructions for this booking")]
    public string? CareInstructions { get; set; }

    public static IEnumerable<ServiceType> ServiceOptions => Enum.GetValues<ServiceType>();

    // Cross-field rules checked by ModelState.IsValid together with the attributes above.
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OldestChildAgeYears < YoungestChildAgeYears)
            yield return new ValidationResult("The oldest child can't be younger than the youngest child.", [nameof(OldestChildAgeYears)]);
        if (NumberOfChildren == 1 && OldestChildAgeYears != YoungestChildAgeYears)
            yield return new ValidationResult("With one child, the youngest and oldest ages should be the same.", [nameof(OldestChildAgeYears)]);
        if (StartsAtLocal is not { } start || EndsAtLocal is not { } end) yield break;
        var nowLocal = DashboardViewModel.Local(DateTimeOffset.UtcNow).DateTime;
        if (start <= nowLocal)
            yield return new ValidationResult("Care must start in the future.", [nameof(StartsAtLocal)]);
        if (end <= start)
            yield return new ValidationResult("Care must end after it starts.", [nameof(EndsAtLocal)]);
        else if ((end - start).TotalHours < 1)
            yield return new ValidationResult("A booking must last at least one hour.", [nameof(EndsAtLocal)]);
        else if ((end - start).TotalDays > MaxDays)
            yield return new ValidationResult($"A single request can last at most {MaxDays} days.", [nameof(EndsAtLocal)]);
    }
}
