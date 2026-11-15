using PetSitting.Data;

namespace PetSitting.Services.Chat;

// Deletes chat messages 24 hours after they were read. Runs every 10 minutes; pages also hide expired messages.
public sealed class MessageCleanupService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<MessageCleanupService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var deleted = await ChatService.DeleteExpiredAsync(scope.ServiceProvider.GetRequiredService<PetSittingDbContext>(), clock.GetUtcNow(), stoppingToken);
                if (deleted > 0) logger.LogInformation("Deleted {Count} chat messages read more than 24 hours ago.", deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Chat message cleanup failed; it will retry."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
