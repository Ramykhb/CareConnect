namespace CareConnect.Contracts.Messaging;

/// <summary>Version 1 contains coordination data only, never addresses, care notes or account identifiers.</summary>
public sealed record CombinedCareEvent(
    EventMetadata Metadata,
    Guid RequestId,
    Guid? PeerRequestId,
    long Revision,
    string RequestStatus,
    string? BookingStatus,
    string? InvitationHash = null,
    DateTimeOffset? InvitationExpiresAtUtc = null);
