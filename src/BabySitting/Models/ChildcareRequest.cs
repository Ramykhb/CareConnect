using System.ComponentModel.DataAnnotations;

namespace BabySitting.Models;

public class ChildcareRequest : Entity
{
    // A linked request may arrive before the originating customer has a local account.
    public Guid? CustomerId { get; set; }
    public ApplicationUser? Customer { get; set; }
    [Required, MaxLength(150)] public string Title { get; set; } = string.Empty;
    [Required, MaxLength(4000)] public string Description { get; set; } = string.Empty;
    public ServiceType ServiceType { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset EndsAtUtc { get; set; }
    [Required, MaxLength(150)] public string City { get; set; } = string.Empty;
    [MaxLength(300)] public string? ServiceAddress { get; set; }
    [Range(typeof(decimal), "0", "99999999")] public decimal Budget { get; set; }
    [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = "USD";
    public RequestStatus Status { get; set; } = RequestStatus.Open;
    public Guid? CorrelationId { get; set; }
    public Guid? ExternalRequestId { get; set; }
    [MaxLength(20)] public string? ExternalPlatform { get; set; }
    public Guid? FamilyProfileId { get; set; }
    public FamilyProfile? FamilyProfile { get; set; }
    [Range(1, 20)] public int NumberOfChildren { get; set; } = 1;
    [Range(0, 17)] public int YoungestChildAgeYears { get; set; }
    [Range(0, 17)] public int OldestChildAgeYears { get; set; }
    // Essential care information; access must be authorized before exposing it.
    [MaxLength(2000)] public string? CareInstructions { get; set; }
    public ICollection<BabysittingOffer> Offers { get; set; } = new List<BabysittingOffer>();
}
