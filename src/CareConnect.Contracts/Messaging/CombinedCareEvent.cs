namespace CareConnect.Contracts.Messaging;

/// <summary>Version 1 contains coordination data only, never addresses, care notes or account identifiers.
/// Event types: Invited, Joined, State and Payment. PaymentStatus is set only on Payment events
/// (Succeeded, Failed or Refunded); amounts and card details are never sent.</summary>
public sealed record CombinedCareEvent(
    EventMetadata Metadata,
    Guid RequestId,
    Guid? PeerRequestId,
    long Revision,
    string RequestStatus,
    string? BookingStatus,
    string? InvitationHash = null,
    DateTimeOffset? InvitationExpiresAtUtc = null,
    string? PaymentStatus = null);
