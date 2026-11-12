using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

public class MessageOutbox : Entity
{
    // Id is also the event ID placed on the outgoing message.
    public Guid CorrelationId { get; set; }
    [Required, MaxLength(200)] public string EventType { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    [Required, MaxLength(200)] public string RoutingKey { get; set; } = string.Empty;
    [Required] public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public int Attempts { get; set; }
    [MaxLength(4000)] public string? LastError { get; set; }
}
