using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.ViewModels;

namespace PetSitting.Services.Bookings;

// Reminds sitters about paid bookings: "Care starts soon" 30 minutes before the start, then "Time to start care" at the start.
// Each reminder is sent once per booking (the notification's SourceKey is unique per user).
public sealed class CareReminderService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CareReminderService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(30);

    // bookingId limits the check to one booking (used by tests); the background job passes null.
    public static async Task<int> RunAsync(PetSittingDbContext db, DateTimeOffset now, CancellationToken ct = default, Guid? bookingId = null)
    {
        var soon = now + Lead;
        var due = await db.PetBookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Confirmed && b.StartsAtUtc <= soon && b.EndsAtUtc > now && (bookingId == null || b.Id == bookingId))
            .Select(b => new { b.Id, b.StartsAtUtc, Sitter = b.SitterProfile.UserId, b.Request.Title }).ToListAsync(ct);
        if (due.Count == 0) return 0;
        var wanted = due.Select(d => (d.Sitter, Key: (d.StartsAtUtc > now ? "care-soon:" : "care-start:") + d.Id, d)).ToList();
        var keys = wanted.Select(w => w.Key).ToList();
        // Compare as (Guid, key) pairs in memory: SQL Server turns GUIDs into upper-case text, .NET into lower-case.
        var sent = (await db.Notifications.AsNoTracking().Where(n => n.SourceKey != null && keys.Contains(n.SourceKey))
            .Select(n => new { n.UserId, n.SourceKey }).ToListAsync(ct)).Select(n => (n.UserId, n.SourceKey!)).ToHashSet();
        var added = 0;
        foreach (var (sitter, key, d) in wanted)
        {
            if (sent.Contains((sitter, key))) continue;
            var starting = d.StartsAtUtc > now;
            db.Notifications.Add(new Notification { UserId = sitter, Category = NotificationCategory.Booking, SourceKey = key, CreatedAtUtc = now,
                Title = starting ? "Care starts soon" : "Time to start care",
                Body = starting ? d.Title + " starts at " + DashboardViewModel.Local(d.StartsAtUtc).ToString("h:mm tt") + ". Start care will unlock on the booking page."
                                : d.Title + " can start now. Open the booking and press Start care.",
                RelativeUrl = "/SitterBookings/Details/" + d.Id });
            added++;
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            // Another app copy sent the same reminder at the same moment; drop ours so it doesn't block later saves.
            foreach (var entry in db.ChangeTracker.Entries<Notification>().Where(e => e.State == EntityState.Added).ToList()) entry.State = EntityState.Detached;
            return 0;
        }   // another app copy sent the same reminder at the same moment
        return added;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sent = await RunAsync(scope.ServiceProvider.GetRequiredService<PetSittingDbContext>(), clock.GetUtcNow(), stoppingToken);
                if (sent > 0) logger.LogInformation("Sent {Count} care reminders.", sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Care reminder check failed; it will retry."); }
        }
    }
}
