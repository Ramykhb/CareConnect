using System.Data;
using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.ViewModels;

namespace BabySitting.Services.Bookings;

public interface IBookingCancellationService
{
    Task<BookingWriteResult> CancelAsync(Guid userId, Guid bookingId, string? reason, string? version, CancellationToken ct = default);
}

// Either side may cancel a booking before care starts. The parent's cancel closes the request;
// the sitter's cancel withdraws their offer and reopens the request for other sitters.
public sealed class BookingCancellationService(BabySittingDbContext db, TimeProvider clock) : IBookingCancellationService
{
    public static bool CanCancel(BabysittingBooking b) => b.Status is BookingStatus.Pending or BookingStatus.AwaitingPayment or BookingStatus.Confirmed;

    public async Task<BookingWriteResult> CancelAsync(Guid userId, Guid bookingId, string? reason, string? version, CancellationToken ct = default)
    {
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 900) return new(false, "Give a reason of up to 900 characters so the other side understands.");
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var booking = await db.BabysittingBookings.Include(b => b.Request).Include(b => b.Offer).Include(b => b.Payments).Include(b => b.SitterProfile).Include(b => b.Customer)
                    .SingleOrDefaultAsync(b => b.Id == bookingId && (b.CustomerId == userId || b.SitterProfile.UserId == userId), ct);
                if (booking is null) return new BookingWriteResult(false, "Booking not found.", true);
                if (booking.Status == BookingStatus.Cancelled) return new(true, "This booking is already cancelled.");
                if (!CanCancel(booking)) return new(false, booking.Status == BookingStatus.InProgress ? "Care has already started, so this booking can't be cancelled." : "Only a booking that hasn't started can be cancelled.");
                if (!Matches(booking.RowVersion, version)) return new(false, "The booking changed. Review the latest details and try again.");
                var now = clock.GetUtcNow();
                var bySitter = booking.SitterProfile.UserId == userId;
                booking.Status = BookingStatus.Cancelled;
                booking.CancelledAtUtc = now;
                booking.CancellationReason = (bySitter ? "Cancelled by the sitter: " : "Cancelled by the parent: ") + reason;
                // Dummy payments only: no money moves, the records show what would be refunded.
                foreach (var p in booking.Payments)
                {
                    if (p.Status == PaymentStatus.Succeeded) p.Status = PaymentStatus.Refunded;
                    else if (p.Status == PaymentStatus.Pending) { p.Status = PaymentStatus.Failed; p.FailureReason = "Booking cancelled."; }
                }
                if (bySitter)
                {
                    booking.Offer.Status = OfferStatus.Withdrawn;
                    booking.Request.Status = booking.Request.StartsAtUtc > now ? RequestStatus.Open : RequestStatus.Expired;
                    db.Notifications.Add(new Notification { Category = NotificationCategory.Booking, UserId = booking.CustomerId, Title = "Your sitter cancelled a booking",
                        Body = $"{booking.Request.Title}: {reason}" + (booking.Request.Status == RequestStatus.Open ? " Your request is open again for other sitters." : ""),
                        RelativeUrl = "/CareRequests/Details/" + booking.RequestId, CreatedAtUtc = now });
                }
                else
                {
                    booking.Request.Status = RequestStatus.Cancelled;
                    // Same key the sitter notification feed uses, so the sitter is told exactly once.
                    db.Notifications.Add(new Notification { Category = NotificationCategory.Booking, SourceKey = "booking-cancelled:" + booking.Id, UserId = booking.SitterProfile.UserId,
                        Title = "Booking cancelled", Body = $"{booking.Customer.FirstName} cancelled {booking.Request.Title}: {reason}", RelativeUrl = "/SitterBookings/Details/" + booking.Id, CreatedAtUtc = now });
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new(true, bySitter ? "Booking cancelled. The parent was notified." : "Booking cancelled. The sitter was notified.");
            });
        }
        catch (DbUpdateConcurrencyException) { return new(false, "The booking changed. Refresh and try again."); }
    }

    private static bool Matches(byte[] actual, string? version)
    {
        try { return actual.Length == 8 && actual.SequenceEqual(Convert.FromBase64String(version ?? "")); } catch (FormatException) { return false; }
    }
}
