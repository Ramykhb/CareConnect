using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using PetSitting.Models;
using PetSitting.Services.Scheduling;

namespace PetSitting.ViewModels;

public sealed class OwnerRequestForm
{
    public List<Guid> PetIds { get; set; } = [];
    public ServiceType? ServiceType { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    [StringLength(150)] public string? City { get; set; } = "";
    [StringLength(120)] public string? Area { get; set; } = "";
    [StringLength(300)] public string? StreetAddress { get; set; } = "";
    [StringLength(60)] public string? Building { get; set; } = "";
    [StringLength(80)] public string? AccessNotes { get; set; } = "";
    [StringLength(4000)] public string? Instructions { get; set; } = "";
    // Keep the stored JSON name so existing drafts retain their agreed initial price.
    [JsonPropertyName("BudgetMin"), Range(typeof(decimal), "1", "99999")] public decimal? Budget { get; set; }
    public Guid? PreferredSitterId { get; set; }
    // Sitters send offers and the customer chooses one, instead of the first sitter to accept being booked.
    public bool CompareOffers { get; set; } = true;
    public bool CareConfirmed { get; set; }

    public (DateTimeOffset Start, DateTimeOffset End)? Times()
    {
        if (StartDate is not { } start || EndDate is not { } end || StartTime is not { } from || EndTime is not { } to) return null;
        return (ScheduleMath.Utc(start, from), ScheduleMath.Utc(end, to));
    }
    public string Address => string.Join(", ", new[] { StreetAddress, Building, Area, AccessNotes }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
    public static string ServiceName(ServiceType? service) => service switch
    {
        Models.ServiceType.PetSitting => "Overnight care", Models.ServiceType.DogWalking => "Dog walking",
        Models.ServiceType.Boarding => "Boarding", Models.ServiceType.DropInVisit => "Drop-in visits", _ => "Not selected"
    };
    public static string ServiceIcon(ServiceType service) => service switch { Models.ServiceType.PetSitting => "moon", Models.ServiceType.DogWalking => "paw", Models.ServiceType.Boarding => "home", _ => "clock" };
}

public sealed class OwnerRequestWizard
{
    public Guid DraftId { get; set; } = Guid.NewGuid();
    public string Version { get; set; } = "";
    public int Step { get; set; } = 1;
    public OwnerRequestForm Form { get; set; } = new();
    public IReadOnlyList<Pet> Pets { get; set; } = [];
    public IReadOnlyList<PetSitterProfile> Sitters { get; set; } = [];
    // Why each sitter is suggested (by sitter id), and whether the order came from the AI.
    public IReadOnlyDictionary<Guid, string> MatchReasons { get; set; } = new Dictionary<Guid, string>();
    public bool MatchedByAi { get; set; }
    public DateTimeOffset? SavedAt { get; set; }
    public static readonly string[] Steps = ["Pet", "Service", "Date & time", "Location", "Details & budget", "Review"];
}

public sealed class OwnerRequestResult
{
    public PetCareRequest Request { get; set; } = null!;
    public OwnerRequestForm? Form { get; set; }
    public PetSitterProfile? PreferredSitter { get; set; }
    public PetBooking? Booking { get; set; }
    public string Reference => "PP-" + Request.Id.ToString("N")[..8].ToUpperInvariant();
}
