using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.ViewModels;

namespace BabySitting.Services.Bookings;

public sealed class ParentBookingService(BabySittingDbContext db, TimeProvider clock)
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
    public static bool CanPay(BabysittingBooking booking) => booking.Status == BookingStatus.AwaitingPayment && !BookingRules.FullyPaid(booking);
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
            return new(true, booking is null ? "Your request was cancelled." : "Booking cancelled. Your sitter was notified. Any recorded demo payments were marked refunded.");
        });
    }
    public async Task<BookingWriteResult> PayAsync(Guid owner, Guid bookingId, Guid token, string? version)
    {
        if (token == Guid.Empty) return new(false, "Reload this page before recording payment.");
        return await Write(async () =>
        {
            var booking = await db.BabysittingBookings.Include(b => b.Payments).Include(b => b.SitterProfile).Include(b => b.Request).SingleOrDefaultAsync(b => b.Id == bookingId && b.CustomerId == owner);
            if (booking is null) return new(false, "Booking not found.", true);
            if (booking.Payments.Any(p => p.IdempotencyKey == token)) return new(true, "This demo payment was already recorded.");
            if (!CanPay(booking)) return new(false, "Only an accepted booking awaiting payment can be paid.");
            if (booking.Request.Status != RequestStatus.Booked || !Matches(booking.RowVersion, version)) return new(false, "The booking changed. Review the latest details before recording payment.");
            var amount = booking.TotalAmount - BookingRules.Paid(booking);
            var payment = new Payment { BookingId = booking.Id, PayerId = owner, Amount = amount, Currency = booking.Currency,
                Status = PaymentStatus.Succeeded, Provider = "Dummy", ProviderTransactionId = "DEMO-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(), IdempotencyKey = token, PaidAtUtc = clock.GetUtcNow() };
            db.Payments.Add(payment); booking.Status = BookingStatus.Confirmed;
            db.Notifications.Add(new() { Category = NotificationCategory.Payment, SourceKey = $"payment:{payment.Id}:{PaymentStatus.Succeeded}", UserId = booking.SitterProfile.UserId,
                Title = "Demo payment recorded", Body = booking.Request.Title + " is paid and confirmed (demo).", RelativeUrl = "/SitterBookings/Details/" + booking.Id });
            return new(true, "Demo payment recorded. No card was charged. Your booking is confirmed.");
        });
    }
    public async Task<BookingWriteResult> ReviewAsync(Guid owner, Guid bookingId, int rating, string? comment)
    {
        if (rating is < 1 or > 5 || comment?.Length > 2000) return new(false, "Choose 1–5 stars and a review of up to 2,000 characters.");
        return await Write(async () =>
        {
            var booking = await db.BabysittingBookings.Include(b => b.Review).Include(b => b.SitterProfile).SingleOrDefaultAsync(b => b.Id == bookingId && b.CustomerId == owner);
            if (booking is null) return new(false, "Booking not found.", true);
            if (booking.Status != BookingStatus.Completed) return new(false, "Reviews are available after care is completed.");
            if (booking.Review is not null) return new(false, "You already reviewed this booking.");
            var review = new Review { BookingId = booking.Id, ReviewerId = owner, Rating = rating, Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(), CreatedAtUtc = clock.GetUtcNow() };
            db.Reviews.Add(review);
            db.Notifications.Add(new() { Category = NotificationCategory.Review, SourceKey = "review:" + review.Id, UserId = booking.SitterProfile.UserId,
                Title = $"New {rating}-star review", Body = review.Comment ?? "A family rated your care.", RelativeUrl = "/Sitter/Reviews#review-" + review.Id });
            return new(true, "Thank you! Your review was published.");
        });
    }
    public async Task<Guid?> AgainAsync(Guid owner, Guid id)
    {
        var model = await DetailsAsync(owner, id); if (model is null) return null;
        var form = model.Draft ?? new ParentRequestForm { City = model.Request.City, Instructions = model.Request.Description, ServiceType = model.Request.ServiceType };
        form.NumberOfChildren = model.Request.NumberOfChildren; form.YoungestChildAgeYears = model.Request.YoungestChildAgeYears; form.OldestChildAgeYears = model.Request.OldestChildAgeYears;
        form.PreferredSitterId = model.Sitter?.Id; form.CareConfirmed = false; form.StartDate = null; form.EndDate = null;
        form.StartTime = TimeOnly.FromDateTime(DashboardViewModel.Local(model.Request.StartsAtUtc).DateTime); form.EndTime = TimeOnly.FromDateTime(DashboardViewModel.Local(model.Request.EndsAtUtc).DateTime);
        if (model.Request.Currency == "USD") { form.BudgetMin = model.Item.Amount; form.BudgetMax = Math.Max(form.BudgetMax ?? model.Item.Amount, model.Item.Amount); }
        else { form.BudgetMin = null; form.BudgetMax = null; }
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
