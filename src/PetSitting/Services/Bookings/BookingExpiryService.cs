using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;

namespace PetSitting.Services.Bookings;

// Every 5 minutes:
//  1. Open requests whose start time passed without a sitter are saved as Expired (pending offers expire too).
//  2. Bookings still waiting for payment at their start time are cancelled (payment deadline).
//  3. Paid bookings whose end time passed without care ever starting are cancelled and the demo payment refunded (no-show).
// Changes are saved through the normal DbContext, so combined-care links sync to the other app and notifications go out live.
public sealed class BookingExpiryService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<BookingExpiryService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // customerId limits the sweep to one customer's records (used by tests); the background job passes null.
    public static async Task<int> RunAsync(PetSittingDbContext db, DateTimeOffset now, CancellationToken ct = default, Guid? customerId = null)
    {
        var changed = 0;
        var expired = await db.PetCareRequests.Include(r => r.Offers).Where(r => r.Status == RequestStatus.Open && r.StartsAtUtc <= now && (customerId == null || r.CustomerId == customerId)).Take(200).ToListAsync(ct);
        foreach (var request in expired)
        {
            request.Status = RequestStatus.Expired;
            foreach (var offer in request.Offers.Where(o => o.Status == OfferStatus.Pending)) { offer.Status = OfferStatus.Expired; offer.DecidedAtUtc = now; }
            if (request.CustomerId is { } customer)
                db.Notifications.Add(new Notification { UserId = customer, Category = NotificationCategory.Request, SourceKey = "request-expired:" + request.Id,
                    Title = "Your request expired", Body = request.Title + " reached its start time without a booked sitter.", RelativeUrl = "/OwnerBookings/Details/" + request.Id, CreatedAtUtc = now });
            changed++;
        }

        var overdue = await db.PetBookings.Include(b => b.Request).Include(b => b.Offer).Include(b => b.Payments).Include(b => b.SitterProfile)
            .Where(b => ((b.Status == BookingStatus.AwaitingPayment && b.StartsAtUtc <= now) || (b.Status == BookingStatus.Confirmed && b.EndsAtUtc <= now)) && (customerId == null || b.CustomerId == customerId))
            .AsSplitQuery().Take(200).ToListAsync(ct);
        foreach (var booking in overdue)
        {
            var unpaid = booking.Status == BookingStatus.AwaitingPayment;
            booking.Status = BookingStatus.Cancelled; booking.CancelledAtUtc = now;
            booking.CancellationReason = unpaid ? "Payment wasn't completed before the start time." : "Care was never started before the end time.";
            booking.Offer.Status = OfferStatus.Withdrawn;
            if (booking.Request.Status == RequestStatus.Booked) booking.Request.Status = RequestStatus.Expired;
            foreach (var payment in booking.Payments)
            {
                if (payment.Status == PaymentStatus.Succeeded) payment.Status = PaymentStatus.Refunded;
                else if (payment.Status == PaymentStatus.Pending) { payment.Status = PaymentStatus.Failed; payment.FailureReason = "Booking cancelled automatically."; }
            }
            var title = unpaid ? "Booking cancelled: not paid in time" : "Booking cancelled: care never started";
            var body = booking.Request.Title + (unpaid ? " wasn't paid before it started." : " ended without care being started. Any payment was refunded.");
            db.Notifications.Add(new Notification { UserId = booking.CustomerId, Category = NotificationCategory.Booking, SourceKey = "booking-auto-cancelled:" + booking.Id,
                Title = title, Body = body, RelativeUrl = "/OwnerBookings/Details/" + booking.RequestId, CreatedAtUtc = now });
            db.Notifications.Add(new Notification { UserId = booking.SitterProfile.UserId, Category = NotificationCategory.Booking, SourceKey = "booking-cancelled:" + booking.Id,
                Title = title, Body = body, RelativeUrl = "/SitterBookings/Details/" + booking.Id, CreatedAtUtc = now });
            changed++;
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first check runs one interval after start-up (not immediately), so short-lived test hosts never sweep shared data.
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var changed = await RunAsync(scope.ServiceProvider.GetRequiredService<PetSittingDbContext>(), clock.GetUtcNow(), stoppingToken);
                if (changed > 0) logger.LogInformation("Expired or cancelled {Count} overdue requests and bookings.", changed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Booking expiry check failed; it will retry."); }
        }
    }
}
