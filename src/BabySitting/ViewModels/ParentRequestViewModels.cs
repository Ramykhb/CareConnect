using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BabySitting.Models;
using BabySitting.Services.Scheduling;

namespace BabySitting.ViewModels;

public sealed class ParentRequestForm
{
    // Children are described by count and age range only (no names, birth dates or schools).
    [Range(1, 20)] public int NumberOfChildren { get; set; } = 1;
    [Range(0, 17)] public int YoungestChildAgeYears { get; set; }
    [Range(0, 17)] public int OldestChildAgeYears { get; set; }
    public ServiceType? ServiceType { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    [StringLength(150)] public string? City { get; set; } = "";
    [StringLength(120)] public string? Area { get; set; } = "";
    [StringLength(140)] public string? StreetAddress { get; set; } = "";
    [StringLength(60)] public string? Building { get; set; } = "";
    [StringLength(80)] public string? AccessNotes { get; set; } = "";
    [StringLength(3000)] public string? Instructions { get; set; } = "";
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
        Models.ServiceType.Babysitting => "Babysitting", Models.ServiceType.AfterSchoolCare => "After-school care",
        Models.ServiceType.OvernightCare => "Overnight care", _ => "Not selected"
    };
    public static string ServiceIcon(ServiceType service) => service switch { Models.ServiceType.OvernightCare => "moon", Models.ServiceType.AfterSchoolCare => "clock", _ => "family" };
}

public sealed class ParentRequestWizard
{
    public Guid DraftId { get; set; } = Guid.NewGuid();
    public string Version { get; set; } = "";
    public int Step { get; set; } = 1;
    public ParentRequestForm Form { get; set; } = new();
    public FamilyProfile? Family { get; set; }
    public IReadOnlyList<BabysitterProfile> Sitters { get; set; } = [];
    // Why each sitter is suggested (by sitter id), and whether the order came from the AI.
    public IReadOnlyDictionary<Guid, string> MatchReasons { get; set; } = new Dictionary<Guid, string>();
    public bool MatchedByAi { get; set; }
    public bool MatchingRequested { get; set; }
    public DateTimeOffset? SavedAt { get; set; }
    public static readonly string[] Steps = ["Children", "Service", "Date & time", "Location", "Details & budget", "Review"];
}

public sealed class ParentRequestResult
{
    public ChildcareRequest Request { get; set; } = null!;
    public ParentRequestForm? Form { get; set; }
    public BabysitterProfile? PreferredSitter { get; set; }
    public BabysittingBooking? Booking { get; set; }
    public string Reference => "CC-" + Request.Id.ToString("N")[..8].ToUpperInvariant();
}
