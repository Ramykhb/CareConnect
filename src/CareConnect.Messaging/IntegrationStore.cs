using CareConnect.Contracts.Messaging;

namespace CareConnect.Messaging;

public sealed record OutgoingMessage(Guid Id, Guid CorrelationId, string RoutingKey, string PayloadJson);

// Implemented separately by each app, against its own database only.
public interface IIntegrationStore
{
    Task<IReadOnlyList<OutgoingMessage>> PendingAsync(CancellationToken ct);
    Task PublishedAsync(Guid id, CancellationToken ct);
    Task FailedAsync(Guid id, CancellationToken ct);
    Task ReceiveAsync(CombinedCareEvent message, CancellationToken ct);
}
