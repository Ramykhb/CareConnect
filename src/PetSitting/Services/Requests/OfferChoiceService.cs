using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.ViewModels;

namespace PetSitting.Services.Requests;

public sealed record OfferOption(Guid OfferId, Guid SitterProfileId, string SitterName, string City, int YearsOfExperience, bool Verified,
    double? Rating, int ReviewCount, decimal Amount, string Currency, string? Message, string? Extra, DateTimeOffset SubmittedAtUtc);

// The customer compares the offers sitters sent for a request and books one of them.
public interface IOfferChoiceService
{
    Task<IReadOnlyList<OfferOption>> OffersAsync(Guid customerId, Guid requestId, CancellationToken ct = default);
    Task<BookingWriteResult> ChooseAsync(Guid customerId, Guid requestId, Guid offerId, string? version, CancellationToken ct = default);
}

public sealed class OfferChoiceService(PetSittingDbContext db, TimeProvider clock) : IOfferChoiceService
{
    public async Task<IReadOnlyList<OfferOption>> OffersAsync(Guid customerId, Guid requestId, CancellationToken ct = default)
    {
        var offers = await db.PetCareOffers.AsNoTracking()
            .Where(o => o.RequestId == requestId && o.Request.CustomerId == customerId && o.Status == OfferStatus.Pending && o.SubmittedAtUtc != null)
            .Select(o => new
            {
                o.Id, o.SitterProfileId, o.SitterProfile.User.FirstName, o.SitterProfile.User.LastName, o.SitterProfile.City, o.SitterProfile.YearsOfExperience,
                Verified = o.SitterProfile.VerificationStatus == VerificationStatus.Verified, o.Amount, o.Currency, o.Message, Submitted = o.SubmittedAtUtc!.Value,
                Extra = (string?)null,
                Ratings = db.Reviews.Where(r => r.Booking.SitterProfileId == o.SitterProfileId && r.ReviewerId == r.Booking.CustomerId).Select(r => (double)r.Rating).ToList()
            }).ToListAsync(ct);
        return offers.Select(o => new OfferOption(o.Id, o.SitterProfileId, o.FirstName + " " + o.LastName, o.City, o.YearsOfExperience, o.Verified,
                o.Ratings.Count == 0 ? null : Math.Round(o.Ratings.Average(), 1), o.Ratings.Count, o.Amount, o.Currency, o.Message, o.Extra, o.Submitted))
            .OrderByDescending(o => o.Verified).ThenByDescending(o => o.Rating ?? 0).ThenByDescending(o => o.YearsOfExperience).ToList();
    }

    public async Task<BookingWriteResult> ChooseAsync(Guid customerId, Guid requestId, Guid offerId, string? version, CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                // Serializable: the request, the chosen sitter's calendar and the other offers must not change underneath the choice.
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var now = clock.GetUtcNow();
                var request = await db.PetCareRequests.Include(r => r.Offers).Include(r => r.Customer).SingleOrDefaultAsync(r => r.Id == requestId && r.CustomerId == customerId, ct);
                if (request is null) return new BookingWriteResult(false, "Request not found.", true);
                if (request.Status != RequestStatus.Open || request.StartsAtUtc <= now) return new(false, "This request is no longer open, so an offer can't be chosen.");
                if (!Matches(request.RowVersion, version)) return new(false, "The request changed. Review the latest offers and try again.");
                var offer = request.Offers.SingleOrDefault(o => o.Id == offerId && o.Status == OfferStatus.Pending && o.SubmittedAtUtc != null);
                if (offer is null) return new(false, "That offer is no longer available. Choose another one.");
                var profile = await db.PetSitterProfiles.Include(p => p.User).Include(p => p.Services).Include(p => p.Availability).Include(p => p.ScheduleExceptions).AsSplitQuery()
                    .SingleAsync(p => p.Id == offer.SitterProfileId, ct);
                var busy = await db.PetBookings.Where(b => b.SitterProfileId == profile.Id && b.StartsAtUtc < request.EndsAtUtc && b.EndsAtUtc > request.StartsAtUtc &&
                    (b.Status == BookingStatus.Pending || b.Status == BookingStatus.AwaitingPayment || b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.InProgress)).ToListAsync(ct);
                if (!profile.User.IsActive || SitterRequestPolicy.AcceptanceBlock(request, profile, busy, now) is not null)
                    return new(false, profile.User.FirstName + " can no longer take these dates. Choose another offer.");

                offer.Status = OfferStatus.Accepted; offer.DecidedAtUtc = now;
                foreach (var other in request.Offers.Where(o => o.Id != offer.Id && o.Status == OfferStatus.Pending))
                {
                    other.Status = OfferStatus.Rejected; other.DecidedAtUtc = now;
                    if (other.SubmittedAtUtc is null) continue;
                    var otherUser = await db.PetSitterProfiles.Where(p => p.Id == other.SitterProfileId).Select(p => p.UserId).SingleAsync(ct);
                    db.Notifications.Add(new Notification { UserId = otherUser, Category = NotificationCategory.Request, SourceKey = "offer-not-chosen:" + other.Id,
                        Title = "Another sitter was chosen", Body = request.Title + " · Thanks for your offer.", RelativeUrl = "/SitterRequests?status=declined", CreatedAtUtc = now });
                }
                request.Status = RequestStatus.Booked;
                var booking = new PetBooking { RequestId = request.Id, OfferId = offer.Id, CustomerId = customerId, SitterProfileId = profile.Id,
                    StartsAtUtc = request.StartsAtUtc, EndsAtUtc = request.EndsAtUtc, TotalAmount = offer.Amount, Currency = offer.Currency,
                    Status = offer.Amount > 0 ? BookingStatus.AwaitingPayment : BookingStatus.Confirmed, CreatedAtUtc = now, CorrelationId = request.CorrelationId };
                db.PetBookings.Add(booking);
                db.Notifications.Add(new Notification { UserId = profile.UserId, Category = NotificationCategory.Request, SourceKey = "offer-chosen:" + offer.Id,
                    Title = request.Customer!.FirstName + " chose your offer", Body = request.Title + " is booked with you.", RelativeUrl = "/SitterBookings/Details/" + booking.Id, CreatedAtUtc = now });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                var next = booking.Status == BookingStatus.AwaitingPayment ? "accepted and waiting for payment" : "confirmed";
                return new(true, "You chose " + profile.User.FirstName + ". Your booking is " + next + ".");
            });
        }
        catch (DbUpdateConcurrencyException) { return new(false, "The offers changed while you were choosing. Review them and try again."); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(false, "This request was already booked."); }
    }

    private static bool Matches(byte[] actual, string? value) { try { return actual.Length == 8 && actual.SequenceEqual(Convert.FromBase64String(value ?? "")); } catch (FormatException) { return false; } }
}
