using System.ComponentModel.DataAnnotations;

namespace BabySitting.Models;

public sealed class ParentRequestDraft : Entity
{
    public Guid CustomerId { get; set; }
    public ApplicationUser Customer { get; set; } = null!;
    [Required, MaxLength(20000)] public string DataJson { get; set; } = "{}";
    [Range(1, 6)] public int Step { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid? SubmittedRequestId { get; set; }
    public ChildcareRequest? SubmittedRequest { get; set; }
}
