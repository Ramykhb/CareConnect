using System.Text;
using System.Text.Json;
using CareConnect.Contracts.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CareConnect.Messaging;

/// <summary>At-least-once transport: confirms before outbox completion or retry acknowledgements.</summary>
public sealed class RabbitMqWorker(IServiceScopeFactory scopes, IOptions<BrokerOptions> configuration,
    ILogger<RabbitMqWorker> log, BrokerHealth health) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = configuration.Value;
        if (!o.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var factory = new ConnectionFactory { HostName = o.HostName, Port = o.Port, VirtualHost = o.VirtualHost,
                    UserName = o.UserName, Password = o.Password, AutomaticRecoveryEnabled = false,
                    RequestedHeartbeat = TimeSpan.FromSeconds(20), Ssl = new SslOption { Enabled = o.UseTls, ServerName = o.HostName } };
                await using var connection = await factory.CreateConnectionAsync("CareConnect-" + o.Platform, stoppingToken);
                await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
                await DeclareTopologyAsync(channel, o.Exchange, stoppingToken);
                health.SetConnected(true);
                log.LogInformation("RabbitMQ connected: {Platform}, queue {Queue}", o.Platform, o.Queue);
                while (!stoppingToken.IsCancellationRequested && channel.IsOpen)
                {
                    using (var scope = scopes.CreateScope())
                    {
                        var store = scope.ServiceProvider.GetRequiredService<IIntegrationStore>();
                        foreach (var message in await store.PendingAsync(stoppingToken))
                        {
                            try
                            {
                                await PublishAsync(channel, o.Exchange, message.RoutingKey, Encoding.UTF8.GetBytes(message.PayloadJson),
                                    message.Id.ToString(), message.CorrelationId.ToString(), null, stoppingToken);
                                await store.PublishedAsync(message.Id, stoppingToken);
                            }
                            catch (Exception) when (!stoppingToken.IsCancellationRequested)
                            {
                                await store.FailedAsync(message.Id, stoppingToken);
                                throw;
                            }
                        }
                    }
                    // Sequential manual-ack consumption bounds memory and preserves channel safety.
                    for (var i = 0; i < 20; i++)
                    {
                        var delivery = await channel.BasicGetAsync(o.Queue, autoAck: false, stoppingToken);
                        if (delivery is null) break;
                        await ReceiveAsync(channel, delivery, o, stoppingToken);
                    }
                    await Task.Delay(500, stoppingToken);
                }
                health.SetConnected(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { health.SetConnected(false); break; }
            catch (Exception ex)
            {
                health.SetConnected(false);
                // Do not log connection strings, message bodies or invitation codes.
                log.LogWarning("RabbitMQ session interrupted ({ErrorType}); retrying in 5 seconds", ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task ReceiveAsync(IChannel channel, BasicGetResult delivery, BrokerOptions o, CancellationToken ct)
    {
        try
        {
            if (delivery.Body.Length > 16_384) throw new InvalidDataException("Oversized integration event.");
            var message = JsonSerializer.Deserialize<CombinedCareEvent>(delivery.Body.Span)
                ?? throw new InvalidDataException("Empty integration event.");
            var peer = o.Platform == "PetSitting" ? "BabySitting" : "PetSitting";
            if (message.Metadata is null || message.Metadata.Source != peer || message.Metadata.Version != 1 ||
                delivery.BasicProperties.MessageId != message.Metadata.EventId.ToString() ||
                delivery.BasicProperties.CorrelationId != message.Metadata.CorrelationId.ToString())
                throw new InvalidDataException("Unexpected event envelope.");
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IIntegrationStore>().ReceiveAsync(message, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var attempt = 0;
            if (delivery.BasicProperties.Headers?.TryGetValue("careconnect-attempt", out var value) == true)
            {
                try { attempt = Math.Clamp(Convert.ToInt32(value), 0, 5); }
                catch (Exception conversion) when (conversion is FormatException or InvalidCastException or OverflowException) { attempt = 5; }
            }
            var permanent = ex is JsonException or InvalidDataException;
            var destination = o.Queue + (permanent || attempt >= 5 ? ".dead" : ".retry");
            await PublishAsync(channel, "", destination, delivery.Body, delivery.BasicProperties.MessageId,
                delivery.BasicProperties.CorrelationId, new Dictionary<string, object?> { ["careconnect-attempt"] = attempt + 1 }, ct);
            log.LogWarning("Integration event {EventId} moved to {Destination} ({ErrorType})",
                delivery.BasicProperties.MessageId, destination, ex.GetType().Name);
        }
        // If processing or the confirmed retry/dead-letter publication fails, the session
        // closes without acknowledging; RabbitMQ redelivers the original delivery.
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
    }

    private static async Task PublishAsync(IChannel channel, string exchange, string key, ReadOnlyMemory<byte> body,
        string? id, string? correlation, IDictionary<string, object?>? headers, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await channel.BasicPublishAsync(exchange, key, mandatory: true, new BasicProperties {
            Persistent = true, ContentType = "application/json", MessageId = id, CorrelationId = correlation,
            Type = "careconnect.combined.v1", Headers = headers }, body, timeout.Token);
    }

    public static async Task DeclareTopologyAsync(IChannel channel, string exchange, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        foreach (var (queue, source) in new[] { ("petsitting.events", "babysitting"), ("babysitting.events", "petsitting") })
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum", ["x-delivery-limit"] = -1 }, cancellationToken: ct);
            await channel.QueueBindAsync(queue, exchange, source + ".combined.#", cancellationToken: ct);
            await channel.QueueDeclareAsync(queue + ".dead", durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum", ["x-delivery-limit"] = -1 }, cancellationToken: ct);
            await channel.QueueDeclareAsync(queue + ".retry", durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum", ["x-dead-letter-strategy"] = "at-least-once",
                    ["x-overflow"] = "reject-publish", ["x-message-ttl"] = 10_000,
                    ["x-dead-letter-exchange"] = "", ["x-dead-letter-routing-key"] = queue }, cancellationToken: ct);
        }
    }
}
