namespace CareConnect.Contracts.Messaging;

/// <summary>Common envelope metadata for versioned cross-application events.</summary>
public sealed record EventMetadata(
    Guid EventId,
    Guid CorrelationId,
    string EventType,
    int Version,
    string Source,
    DateTimeOffset OccurredAtUtc);
