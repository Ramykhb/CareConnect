using System.ComponentModel.DataAnnotations;
namespace BabySitting.Models;

public sealed class CombinedCareLink : Entity
{
    public Guid? LocalRequestId { get; set; }
    public Guid? PeerRequestId { get; set; }
    [Required, MaxLength(20)] public string OriginPlatform { get; set; } = "";
    [Required, MaxLength(64)] public string InvitationHash { get; set; } = "";
    public DateTimeOffset InvitationExpiresAtUtc { get; set; }
    [MaxLength(2048)] public string? ProtectedInvitationCode { get; set; }
    public long LocalRevision { get; set; }
    public long PeerRevision { get; set; }
    [MaxLength(20)] public string? PeerRequestStatus { get; set; }
    [MaxLength(20)] public string? PeerBookingStatus { get; set; }
    public DateTimeOffset? LastPeerUpdateAtUtc { get; set; }
    [MaxLength(300)] public string? CancellationIssue { get; set; }
}
