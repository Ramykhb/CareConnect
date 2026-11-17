using RabbitMQ.Client;

namespace CareConnect.Messaging;

public static class DeadLetterReplay
{
    // Explicit operator command. Originals stay unacknowledged until the replay is confirmed.
    public static async Task<int> RunAsync(BrokerOptions o, CancellationToken ct = default)
    {
        if (!o.Enabled) throw new InvalidOperationException("Enable and configure RabbitMQ before replaying messages.");
        var factory = new ConnectionFactory { HostName = o.HostName, Port = o.Port, VirtualHost = o.VirtualHost,
            UserName = o.UserName, Password = o.Password, Ssl = new SslOption { Enabled = o.UseTls, ServerName = o.HostName } };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), ct);
        await RabbitMqWorker.DeclareTopologyAsync(channel, o.Exchange, ct);
        var count = 0;
        while (count < 100)
        {
            var message = await channel.BasicGetAsync(o.Queue + ".dead", false, ct);
            if (message is null) break;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var properties = new BasicProperties { Persistent = true, ContentType = "application/json",
                MessageId = message.BasicProperties.MessageId, CorrelationId = message.BasicProperties.CorrelationId,
                Headers = new Dictionary<string, object?> { ["careconnect-attempt"] = 0 } };
            await channel.BasicPublishAsync("", o.Queue, true, properties, message.Body, timeout.Token);
            await channel.BasicAckAsync(message.DeliveryTag, false, ct);
            count++;
        }
        return count;
    }
}
