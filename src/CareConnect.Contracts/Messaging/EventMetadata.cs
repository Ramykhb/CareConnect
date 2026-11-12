namespace CareConnect.Contracts.Messaging;

/// <summary>Common envelope metadata; payloads are versioned separately in Phase 5.</summary>
public sealed record EventMetadata(
    Guid EventId,
    Guid CorrelationId,
    string EventType,
    int Version,
    string Source,
    DateTimeOffset OccurredAtUtc);
