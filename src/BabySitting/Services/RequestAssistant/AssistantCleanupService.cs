using Microsoft.EntityFrameworkCore;
using BabySitting.Data;

namespace BabySitting.Services.RequestAssistant;

public sealed class AssistantCleanupService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AssistantCleanupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
                var cutoff = clock.GetUtcNow().AddHours(-24);
                await db.RequestAssistantConversations.Where(c => c.UpdatedAtUtc < cutoff).ExecuteDeleteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning("Request assistant cleanup failed ({ErrorType}).", e.GetType().Name); }
        }
    }
}
