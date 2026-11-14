using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.ViewModels;

namespace BabySitting.Services.Bookings;

public interface IParentPaymentService
{
    Task<BookingWriteResult> PayAsync(Guid parentId, Guid bookingId, Guid clientToken, string? version, CancellationToken ct = default);
    Task<BookingWriteResult> ReviewAsync(Guid parentId, Guid bookingId, int rating, string? comment, CancellationToken ct = default);
    Task<IReadOnlyList<Notification>> NotificationsAsync(Guid userId, CancellationToken ct = default);
    Task<bool> MarkReadAsync(Guid userId, Guid? id, CancellationToken ct = default);
}

// The parent's side of a booking: a demo payment, a review after care, and in-app notifications.
public sealed class ParentPaymentService(BabySittingDbContext db, TimeProvider clock) : IParentPaymentService
{
    public static bool CanPay(BabysittingBooking b) => b.Status == BookingStatus.AwaitingPayment && !BookingRules.FullyPaid(b);
    public static bool CanReview(BabysittingBooking b) => b.Status == BookingStatus.Completed && b.Review is null;

    // Demo payment: no card details are collected and no money moves. The client token makes a
    // double-clicked or resubmitted form record the payment only once (unique IdempotencyKey).
    public async Task<BookingWriteResult> PayAsync(Guid parentId, Guid bookingId, Guid clientToken, string? version, CancellationToken ct = default)
    {
        if (clientToken == Guid.Empty) return new(false, "Reload the page and try again.");
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var booking = await db.BabysittingBookings.Include(b => b.Payments).Include(b => b.SitterProfile).Include(b => b.Request)
                    .SingleOrDefaultAsync(b => b.Id == bookingId && b.CustomerId == parentId, ct);
                if (booking is null) return new BookingWriteResult(false, "Booking not found.", true);
                if (booking.Payments.Any(p => p.IdempotencyKey == clientToken)) return new(true, "This payment was already recorded.");
                if (!CanPay(booking)) return new(false, BookingRules.FullyPaid(booking) ? "This booking is already paid." : "Only an accepted booking waiting for payment can be paid.");
                if (!Matches(booking.RowVersion, version)) return new(false, "The booking changed. Review the latest details and try again.");
                var now = clock.GetUtcNow();
                var amount = booking.TotalAmount - BookingRules.Paid(booking);
                var payment = new Payment { BookingId = booking.Id, PayerId = parentId, Amount = amount, Currency = booking.Currency, Status = PaymentStatus.Succeeded,
                    Provider = "Dummy", ProviderTransactionId = "DEMO-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(), IdempotencyKey = clientToken, PaidAtUtc = now, CreatedAtUtc = now };
                db.Payments.Add(payment);
                booking.Status = BookingStatus.Confirmed;
                // Same key as the sitter's notification feed, so the sitter is told once.
                db.Notifications.Add(new Notification { Category = NotificationCategory.Payment, SourceKey = $"payment:{payment.Id}:{PaymentStatus.Succeeded}", UserId = booking.SitterProfile.UserId,
                    Title = "Payment received · " + DashboardViewModel.Money(amount, booking.Currency), Body = booking.Request.Title + " is paid and confirmed.", RelativeUrl = "/SitterBookings/Details/" + booking.Id, CreatedAtUtc = now });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new(true, $"Payment of {DashboardViewModel.Money(amount, booking.Currency)} recorded (demo — no real charge). Your booking is confirmed.");
            });
        }
        catch (DbUpdateConcurrencyException) { return new(false, "The booking changed. Refresh and try again."); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(true, "This payment was already recorded."); }
    }

    public async Task<BookingWriteResult> ReviewAsync(Guid parentId, Guid bookingId, int rating, string? comment, CancellationToken ct = default)
    {
        if (rating is < 1 or > 5) return new(false, "Choose a rating from 1 to 5 stars.");
        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (comment?.Length > 2000) return new(false, "Keep your review under 2,000 characters.");
        var booking = await db.BabysittingBookings.Include(b => b.Review).Include(b => b.SitterProfile).SingleOrDefaultAsync(b => b.Id == bookingId && b.CustomerId == parentId, ct);
        if (booking is null) return new(false, "Booking not found.", true);
        if (booking.Review is not null) return new(false, "You already reviewed this booking.");
        if (booking.Status != BookingStatus.Completed) return new(false, "You can review a booking once care is completed.");
        var now = clock.GetUtcNow();
        var review = new Review { BookingId = booking.Id, ReviewerId = parentId, Rating = rating, Comment = comment, CreatedAtUtc = now };
        db.Reviews.Add(review);
        db.Notifications.Add(new Notification { Category = NotificationCategory.Review, SourceKey = "review:" + review.Id, UserId = booking.SitterProfile.UserId,
            Title = $"New {rating}-star review", Body = comment ?? "A family rated your care.", RelativeUrl = "/Sitter/Reviews#review-" + review.Id, CreatedAtUtc = now });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(false, "You already reviewed this booking."); }
        return new(true, "Thank you! Your review was published.");
    }

    public async Task<IReadOnlyList<Notification>> NotificationsAsync(Guid userId, CancellationToken ct = default) =>
        await db.Notifications.AsNoTracking().Where(n => n.UserId == userId).OrderByDescending(n => n.CreatedAtUtc).Take(100).ToListAsync(ct);

    // Marks one notification (or all, when id is null) as read; false if the id is not the user's.
    public async Task<bool> MarkReadAsync(Guid userId, Guid? id, CancellationToken ct = default)
    {
        var mine = db.Notifications.Where(n => n.UserId == userId);
        if (id is not null && !await mine.AnyAsync(n => n.Id == id, ct)) return false;
        await mine.Where(n => n.ReadAtUtc == null && (id == null || n.Id == id)).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, clock.GetUtcNow()), ct);
        return true;
    }

    private static bool Matches(byte[] actual, string? version)
    {
        try { return actual.Length == 8 && actual.SequenceEqual(Convert.FromBase64String(version ?? "")); } catch (FormatException) { return false; }
    }
}
