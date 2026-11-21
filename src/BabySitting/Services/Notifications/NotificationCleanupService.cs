using Microsoft.EntityFrameworkCore;
using BabySitting.Data;

namespace BabySitting.Services.Notifications;

// Deletes notifications that were read and are older than 90 days. Unread ones are kept, however old.
// The sitter notification sync skips history older than the same window, so deleted items never come back.
public sealed class NotificationCleanupService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<NotificationCleanupService> logger) : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public static Task<int> DeleteExpiredAsync(BabySittingDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var cutoff = now - Retention;
        return db.Notifications.Where(n => !n.EmailPending && n.ReadAtUtc != null && n.CreatedAtUtc < cutoff).ExecuteDeleteAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var deleted = await DeleteExpiredAsync(scope.ServiceProvider.GetRequiredService<BabySittingDbContext>(), clock.GetUtcNow(), stoppingToken);
                if (deleted > 0) logger.LogInformation("Deleted {Count} read notifications older than 90 days.", deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Notification cleanup failed; it will retry."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
