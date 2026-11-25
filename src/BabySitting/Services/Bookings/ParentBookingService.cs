using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Payments;
using BabySitting.ViewModels;

namespace BabySitting.Services.Bookings;

public sealed class ParentBookingService(BabySittingDbContext db, TimeProvider clock, IStripePaymentService? stripe = null)
{
    private static IQueryable<ChildcareRequest> Include(IQueryable<ChildcareRequest> query) => query.Include(r => r.FamilyProfile)
        .Include(r => r.Offers).ThenInclude(o => o.SitterProfile).ThenInclude(s => s.User)
        .Include(r => r.Offers).ThenInclude(o => o.SitterProfile).ThenInclude(s => s.Services).AsSplitQuery();
    private static IQueryable<BabysittingBooking> Bookings(IQueryable<BabysittingBooking> query) => query.Include(b => b.Payments).Include(b => b.Review).Include(b => b.SitterProfile).ThenInclude(s => s.User).Include(b => b.SitterProfile).ThenInclude(s => s.Services).AsSplitQuery();
    private static BabysittingBooking? Pick(IEnumerable<BabysittingBooking> bookings) => bookings.OrderByDescending(b => b.Status is not (BookingStatus.Cancelled or BookingStatus.Rejected)).ThenByDescending(b => b.CreatedAtUtc).FirstOrDefault();
    private static ParentBookingItem Item(ChildcareRequest request, BabysittingBooking? booking, DateTimeOffset now) => new()
    {
        Request = request, Booking = booking, Now = now,
        Sitter = booking?.SitterProfile ?? request.Offers.OrderByDescending(o => o.CreatedAtUtc).Select(o => o.SitterProfile).FirstOrDefault()
    };
    public async Task<ParentBookingsViewModel> ListAsync(Guid owner, string? status, string? search, Guid? petId, ServiceType? service, int days, int page)
    {
        var now = clock.GetUtcNow();
        var requests = await Include(db.ChildcareRequests.AsNoTracking().Where(r => r.CustomerId == owner)).OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.Id).ToListAsync();
        var bookings = await Bookings(db.BabysittingBookings.AsNoTracking().Where(b => b.CustomerId == owner)).ToListAsync();
        var text = search?.Trim(); if (text?.Length > 150) text = text[..150];
        days = days is 30 or 90 or 365 ? days : 0;
        service = service is not null && Enum.IsDefined(service.Value) ? service : null;
        var rows = requests.Select(r => Item(r, Pick(bookings.Where(b => b.RequestId == r.Id)), now)).Where(i =>
            (service is null || i.Request.ServiceType == service)
            && (days == 0 || i.Request.StartsAtUtc >= now.AddDays(-days))
            && (string.IsNullOrWhiteSpace(text) || (i.ChildrenSummary + " " + i.SitterName + " " + i.Reference + " " + i.Request.Title + " " + i.Request.Id).Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new() { Items = rows, Filter = ParentBookingsViewModel.Tabs.Any(t => t.Key == status) ? status! : "all", Search = text, PetId = petId, Service = service, Days = days, Page = page };
    }
    public async Task<ParentBookingDetails?> DetailsAsync(Guid owner, Guid id)
    {
        var request = await Include(db.ChildcareRequests.AsNoTracking().Where(r => r.CustomerId == owner && (r.Id == id || db.BabysittingBookings.Any(b => b.Id == id && b.RequestId == r.Id && b.CustomerId == owner)))).SingleOrDefaultAsync();
        if (request is null) return null;
        var booking = Pick(await Bookings(db.BabysittingBookings.AsNoTracking().Where(b => b.RequestId == request.Id && b.CustomerId == owner)).Include(b => b.Updates).Include(b => b.Offer).ToListAsync());
        var item = Item(request, booking, clock.GetUtcNow());
        var json = await db.ParentRequestDrafts.Where(d => d.CustomerId == owner && d.SubmittedRequestId == request.Id).Select(d => d.DataJson).SingleOrDefaultAsync();
        var draft = json is null ? null : JsonSerializer.Deserialize<ParentRequestForm>(json);
        if (item.Sitter is null && draft?.PreferredSitterId is { } sitterId)
        {
            var sitter = await db.BabysitterProfiles.AsNoTracking().Include(s => s.User).Include(s => s.Services).SingleOrDefaultAsync(s => s.Id == sitterId);
            item = new() { Request = request, Booking = booking, Now = item.Now, Sitter = sitter };
        }
        var ratings = item.Sitter is null ? [] : await db.Reviews.AsNoTracking().Where(r => r.Booking.SitterProfileId == item.Sitter.Id && r.ReviewerId == r.Booking.CustomerId && r.Booking.Status == BookingStatus.Completed).Select(r => r.Rating).ToArrayAsync();
        return new() { Item = item, Draft = draft, ReviewCount = ratings.Length, AverageRating = ratings.Length == 0 ? null : ratings.Average() };
    }
    public static bool CanPay(BabysittingBooking booking, DateTimeOffset? now = null) => ParentPaymentService.CanPay(booking, now);
    public static bool CanCancel(ChildcareRequest request, BabysittingBooking? booking, DateTimeOffset now) =>
        booking is null ? request.Status == RequestStatus.Open && request.StartsAtUtc > now
        : request.Status is not (RequestStatus.Cancelled or RequestStatus.Completed) && booking.Status is BookingStatus.Pending or BookingStatus.AwaitingPayment or BookingStatus.Confirmed
            && !booking.Payments.Any(p => p.Provider != "Dummy" && p.Status is PaymentStatus.Succeeded or PaymentStatus.Pending);

    public async Task<BookingWriteResult> CancelAsync(Guid owner, Guid requestId, string? version, string? reason)
    {
        reason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 900) return new(false, "Enter a cancellation reason of up to 900 characters.");
        return await Write(async () =>
        {
            var request = await db.ChildcareRequests.Include(r => r.Offers).ThenInclude(o => o.SitterProfile).SingleOrDefaultAsync(r => r.Id == requestId && r.CustomerId == owner);
            if (request is null) return new(false, "Request not found.", true);
            var booking = Pick(await db.BabysittingBookings.Include(b => b.Payments).Where(b => b.RequestId == requestId && b.CustomerId == owner).ToListAsync());
            if (request.Status == RequestStatus.Cancelled) return new(true, "This request is already cancelled.");
            if (!CanCancel(request, booking, clock.GetUtcNow())) return new(false, "This request or booking can no longer be cancelled here.");
            if (!Matches(booking?.RowVersion ?? request.RowVersion, version)) return new(false, "The status changed. Review the latest details before cancelling.");
            var now = clock.GetUtcNow();
            request.Status = RequestStatus.Cancelled;
            if (booking is not null)
            {
                booking.Status = BookingStatus.Cancelled; booking.CancelledAtUtc = now; booking.CancellationReason = "Cancelled by the owner: " + reason;
                foreach (var payment in booking.Payments.Where(p => p.Provider == "Dummy"))
                {
                    if (payment.Status == PaymentStatus.Succeeded) payment.Status = PaymentStatus.Refunded;
                    else if (payment.Status == PaymentStatus.Pending) { payment.Status = PaymentStatus.Failed; payment.FailureReason = "Booking cancelled."; }
                }
            }
            foreach (var offer in request.Offers.Where(o => o.Status is OfferStatus.Pending or OfferStatus.Accepted))
            {
                if (offer.Status == OfferStatus.Pending) offer.DecidedAtUtc = now;
                offer.Status = OfferStatus.Withdrawn;
                db.Notifications.Add(new() { Category = NotificationCategory.Booking, SourceKey = "owner-cancelled:" + request.Id + ":" + offer.SitterProfileId,
                    UserId = offer.SitterProfile.UserId, Title = "A parent cancelled a request", Body = request.Title + ": " + reason,
                    RelativeUrl = booking is not null && booking.SitterProfileId == offer.SitterProfileId ? "/SitterBookings/Details/" + booking.Id : "/SitterRequests?id=" + request.Id, CreatedAtUtc = now });
            }
            return new(true, booking is null ? "Your request was cancelled." : "Booking cancelled. Your sitter was notified. Any payments were refunded.");
        });
    }
    // Paying and reviewing live in ParentPaymentService only, so both pages follow the same rules.
    public Task<StripeIntentResult> CreatePaymentIntentAsync(Guid owner, Guid bookingId, Guid token, string? version) =>
        new ParentPaymentService(db, clock, stripe).CreatePaymentIntentAsync(owner, bookingId, token, version);
    public Task<BookingWriteResult> PayAsync(Guid owner, Guid bookingId, Guid token, string? version, string? paymentIntentId = null) =>
        new ParentPaymentService(db, clock, stripe).PayAsync(owner, bookingId, token, version, paymentIntentId);
    public Task<BookingWriteResult> ReviewAsync(Guid owner, Guid bookingId, int rating, string? comment) =>
        new ParentPaymentService(db, clock).ReviewAsync(owner, bookingId, rating, comment);
    public async Task<Guid?> AgainAsync(Guid owner, Guid id)
    {
        var model = await DetailsAsync(owner, id); if (model is null) return null;
        var form = model.Draft ?? new ParentRequestForm { City = model.Request.City, Instructions = model.Request.Description, ServiceType = model.Request.ServiceType };
        form.NumberOfChildren = model.Request.NumberOfChildren; form.YoungestChildAgeYears = model.Request.YoungestChildAgeYears; form.OldestChildAgeYears = model.Request.OldestChildAgeYears;
        form.PreferredSitterId = model.Sitter?.Id; form.CareConfirmed = false; form.StartDate = null; form.EndDate = null;
        form.StartTime = TimeOnly.FromDateTime(DashboardViewModel.Local(model.Request.StartsAtUtc).DateTime); form.EndTime = TimeOnly.FromDateTime(DashboardViewModel.Local(model.Request.EndsAtUtc).DateTime);
        form.Budget = model.Request.Currency == "USD" ? model.Item.Amount : null;
        var draft = new ParentRequestDraft { CustomerId = owner, DataJson = JsonSerializer.Serialize(form), Step = 1, UpdatedAtUtc = clock.GetUtcNow() };
        db.ParentRequestDrafts.Add(draft); await db.SaveChangesAsync(); return draft.Id;
    }
    private async Task<BookingWriteResult> Write(Func<Task<BookingWriteResult>> action)
    {
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                var result = await action(); if (!result.Succeeded) return result;
                await db.SaveChangesAsync(); await tx.CommitAsync(); return result;
            });
        }
        catch (DbUpdateConcurrencyException) { return new(false, "This booking changed. Refresh and try again."); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(false, "This action was already recorded. Refresh to see the latest details."); }
    }
    private static bool Matches(byte[] actual, string? version) { try { return actual.Length == 8 && actual.SequenceEqual(Convert.FromBase64String(version ?? "")); } catch (FormatException) { return false; } }
}
