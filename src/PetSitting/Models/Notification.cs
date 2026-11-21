using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

public class Notification : Entity
{
    public NotificationCategory Category { get; set; }
    [MaxLength(120)] public string? SourceKey { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    [Required, MaxLength(150)] public string Title { get; set; } = string.Empty;
    [Required, MaxLength(2000)] public string Body { get; set; } = string.Empty;
    [MaxLength(500)] public string? RelativeUrl { get; set; }
    public DateTimeOffset? ReadAtUtc { get; set; }
    public bool EmailPending { get; set; }
    public int EmailAttempts { get; set; }
    public DateTimeOffset? EmailNextAttemptAtUtc { get; set; }
    public DateTimeOffset? EmailSentAtUtc { get; set; }
    public Guid? EmailLeaseId { get; set; }
    public DateTimeOffset? EmailLeaseUntilUtc { get; set; }
    [MaxLength(100)] public string? EmailLastError { get; set; }
}
