using System.ComponentModel.DataAnnotations;
using PetSitting.Models;
using PetSitting.Services.Scheduling;

namespace PetSitting.ViewModels;

public sealed class RequestEditViewModel : IValidatableObject
{
    [Required] public string Version { get; set; } = "";
    [Required, StringLength(150)] public string Title { get; set; } = "";
    [Required, StringLength(4000)] public string Description { get; set; } = "";
    [EnumDataType(typeof(ServiceType))] public ServiceType ServiceType { get; set; }
    public List<Guid> PetIds { get; set; } = [];
    public IReadOnlyList<Pet> AvailablePets { get; set; } = [];
    [Required, Display(Name = "Starts (Beirut time)")] public DateTime? StartsAtLocal { get; set; }
    [Required, Display(Name = "Ends (Beirut time)")] public DateTime? EndsAtLocal { get; set; }
    [Required, StringLength(150)] public string City { get; set; } = "";
    [StringLength(300), Display(Name = "Private service address")] public string? ServiceAddress { get; set; }
    [Range(typeof(decimal), "1", "99999"), Display(Name = "Total budget")] public decimal Budget { get; set; }
    // Display only; the request's currency cannot be changed by this form.
    public string Currency { get; set; } = "USD";

    public (DateTimeOffset Start, DateTimeOffset End)? Times() => StartsAtLocal is { } s && EndsAtLocal is { } e
        ? (ScheduleMath.Utc(DateOnly.FromDateTime(s), TimeOnly.FromDateTime(s)), ScheduleMath.Utc(DateOnly.FromDateTime(e), TimeOnly.FromDateTime(e))) : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PetIds.Count is < 1 or > 20 || PetIds.Distinct().Count() != PetIds.Count)
            yield return new("Choose between 1 and 20 different pets.", [nameof(PetIds)]);
        if (ServiceType != ServiceType.Boarding && string.IsNullOrWhiteSpace(ServiceAddress))
            yield return new("Enter the address where care is needed.", [nameof(ServiceAddress)]);
        (DateTimeOffset Start, DateTimeOffset End)? times = null;
        var invalidTime = false;
        try { times = Times(); } catch (ArgumentException) { invalidTime = true; }
        if (invalidTime) yield return new("This local time is unavailable due to daylight saving. Choose another time.", [nameof(StartsAtLocal)]);
        if (times is not { } t) yield break;
        var now = (validationContext.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System).GetUtcNow();
        if (t.Start <= now) yield return new("Care must start in the future.", [nameof(StartsAtLocal)]);
        if (t.End <= t.Start || (t.End - t.Start).TotalDays > 14)
            yield return new("Care must end after it starts and last at most 14 days.", [nameof(EndsAtLocal)]);
    }
}
