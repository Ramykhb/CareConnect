using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

public class PetCareOffer : Entity
{
    public Guid RequestId { get; set; }
    public PetCareRequest Request { get; set; } = null!;
    public Guid SitterProfileId { get; set; }
    public PetSitterProfile SitterProfile { get; set; } = null!;
    [Range(typeof(decimal), "0", "99999999")] public decimal Amount { get; set; }
    [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = "USD";
    [MaxLength(2000)] public string? Message { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public OfferStatus Status { get; set; } = OfferStatus.Pending;
}
