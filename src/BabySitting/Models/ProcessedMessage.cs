using System.ComponentModel.DataAnnotations;

namespace BabySitting.Models;

public class ProcessedMessage
{
    public Guid EventId { get; set; }
    [Required, MaxLength(200)] public string Consumer { get; set; } = string.Empty;
    public Guid CorrelationId { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
