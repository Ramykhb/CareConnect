using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CareConnect.Messaging;

public sealed class BrokerHealth(IOptions<BrokerOptions> options) : IHealthCheck
{
    private int connected;
    public void SetConnected(bool value) => Interlocked.Exchange(ref connected, value ? 1 : 0);
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(!options.Value.Enabled ? HealthCheckResult.Healthy("Messaging disabled.") :
            Volatile.Read(ref connected) == 1 ? HealthCheckResult.Healthy("RabbitMQ session active.") :
            HealthCheckResult.Unhealthy("RabbitMQ unavailable; outgoing updates remain in the database outbox."));
}
