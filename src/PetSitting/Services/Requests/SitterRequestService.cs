using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.ViewModels;

namespace PetSitting.Services.Requests;

public interface ISitterRequestService
{
    Task<SitterRequestsViewModel> LoadAsync(ApplicationUser account, CancellationToken ct = default);
    Task<RequestDecisionResult> DecideAsync(ApplicationUser account, RequestDecisionForm form, CancellationToken ct = default);
}

public sealed class SitterRequestService(PetSittingDbContext db, TimeProvider clock) : ISitterRequestService
{
    public async Task<SitterRequestsViewModel> LoadAsync(ApplicationUser account, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var profile = await db.PetSitterProfiles.AsNoTracking().Include(p => p.Services).Include(p => p.Availability).Include(p => p.ScheduleExceptions).AsSplitQuery().SingleOrDefaultAsync(p => p.UserId == account.Id, ct);
        var city = profile?.City ?? account.City;
        var services = profile?.Services.Select(s => s.ServiceType).ToArray() ?? [];
        var requests = await db.PetCareRequests.AsNoTracking()
            .Where(r => r.CustomerId != null && r.CustomerId != account.Id &&
                (r.Offers.Any(o => o.SitterProfile.UserId == account.Id) ||
                 (r.Customer!.IsActive && r.Status == RequestStatus.Open && r.StartsAtUtc > now && city != null && r.City == city && (services.Length == 0 || services.Contains(r.ServiceType)))))
            .Include(r => r.Customer).Include(r => r.Pets).ThenInclude(p => p.Pet)
            .Include(r => r.Offers.Where(o => o.SitterProfile.UserId == account.Id))
            .AsSplitQuery().OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.Id).ToListAsync(ct);
        var bookings = await db.PetBookings.AsNoTracking().Where(b => b.SitterProfile.UserId == account.Id).Include(b => b.Payments).ToListAsync(ct);
        var items = requests.Select(request =>
        {
            var offer = request.Offers.SingleOrDefault();
            var booking = bookings.Where(b => b.RequestId == request.Id).OrderByDescending(b => b.CreatedAtUtc).FirstOrDefault();
            var state = SitterRequestPolicy.State(request, offer, booking, now);
            var open = state is IncomingRequestState.New or IncomingRequestState.Pending;
            var block = SitterRequestPolicy.AcceptanceBlock(request, profile, bookings, now);
            var amount = offer?.Amount ?? request.Budget;
            var currency = offer?.Currency ?? request.Currency;
            var checks = new List<RequestFitCheck>
            {
                new(SitterRequestPolicy.Overlaps(request, bookings) ? "These dates overlap an active booking" : "No conflicting bookings for the requested dates", !SitterRequestPolicy.Overlaps(request, bookings)),
                new((profile is not null && (profile.Availability.Count > 0 || profile.ScheduleExceptions.Count > 0)) ? SitterRequestPolicy.Covered(request, profile!.Availability, profile.ScheduleExceptions) ? "The dates fit your saved availability" : "The dates are outside your saved availability" : "No date restrictions have been set", (profile is not null && (profile.Availability.Count > 0 || profile.ScheduleExceptions.Count > 0)) ? SitterRequestPolicy.Covered(request, profile!.Availability, profile.ScheduleExceptions) : null),
                new($"In your service city · {request.City}", SitterRequestPolicy.InServiceArea(request, city, services)),
                new(profile is not null && profile.Currency == currency ? amount >= profile.HourlyRate * (decimal)(request.EndsAtUtc - request.StartsAtUtc).TotalHours ? "The offer covers your listed hourly rate" : "The offer is below your listed hourly rate" : "Review the offered amount before accepting", profile is not null && profile.Currency == currency ? amount >= profile.HourlyRate * (decimal)(request.EndsAtUtc - request.StartsAtUtc).TotalHours : null)
            };
            return new IncomingRequestViewModel
            {
                Id = request.Id, Version = SitterRequestPolicy.Version(request, offer), Title = request.Title,
                OwnerName = $"{request.Customer!.FirstName} {request.Customer.LastName}", OwnerFirstName = request.Customer.FirstName, OwnerSince = request.Customer.CreatedAtUtc.Year,
                ServiceType = request.ServiceType, City = request.City,
                // Do not put an unaccepted request's exact address into HTML or refresh responses.
                Address = state == IncomingRequestState.Accepted ? request.ServiceAddress : null,
                Note = request.Description, Amount = amount, Currency = currency, StartsAtUtc = request.StartsAtUtc, EndsAtUtc = request.EndsAtUtc, CreatedAtUtc = request.CreatedAtUtc,
                State = state, BookingId = booking?.Id, BookingStatus = booking?.Status == BookingStatus.AwaitingPayment && PetSitting.Services.Bookings.BookingRules.FullyPaid(booking) ? BookingStatus.Confirmed : booking?.Status, AcceptedAtUtc = state == IncomingRequestState.Accepted ? booking?.CreatedAtUtc : null,
                CanAccept = open && block is null, CanDecline = open && profile is not null, BlockReason = open ? block : null, FitChecks = checks,
                Pets = request.Pets.Select(p => new IncomingPetViewModel(p.Pet.Name, p.Pet.Species, p.Pet.Breed, p.Pet.AgeYears, p.Pet.MedicalNotes, p.Pet.FeedingInstructions, p.Pet.CareInstructions)).ToArray()
            };
        }).ToArray();
        return new SitterRequestsViewModel { Account = account, Profile = profile, Now = now, Items = items, Unread = await db.Notifications.CountAsync(n => n.UserId == account.Id && n.ReadAtUtc == null, ct) };
    }

    public async Task<RequestDecisionResult> DecideAsync(ApplicationUser account, RequestDecisionForm form, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(form.Version)) return new(false, "Reload the inbox before sending a decision.");
        byte[] version;
        try { version = Convert.FromBase64String(form.Version); }
        catch (FormatException) { return new(false, "The request version is invalid. Reload the inbox and try again."); }
        if (version.Length != 16 || form.RequestId == Guid.Empty || form.Decision is not ("accept" or "decline")) return new(false, "The request decision is invalid.");
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                // Serializable reads protect both request assignment and the sitter's booking date range.
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var now = clock.GetUtcNow();
                var profile = await db.PetSitterProfiles.Include(p => p.Services).Include(p => p.Availability).Include(p => p.ScheduleExceptions).AsSplitQuery().SingleOrDefaultAsync(p => p.UserId == account.Id, ct);
                if (profile is null || !account.IsActive) return new RequestDecisionResult(false, "Set up an active sitter profile before responding to requests.");
                var request = await db.PetCareRequests.Include(r => r.Customer).SingleOrDefaultAsync(r => r.Id == form.RequestId, ct);
                if (request is null || request.CustomerId is null || request.CustomerId == account.Id) return new(false, "Request not found.", NotFound: true);
                var offer = await db.PetCareOffers.SingleOrDefaultAsync(o => o.RequestId == request.Id && o.SitterProfileId == profile.Id, ct);
                // Previously decided items stay accessible even if the sitter subsequently changes city.
                if (offer is null && !SitterRequestPolicy.InServiceArea(request, profile.City, profile.Services.Select(s => s.ServiceType))) return new(false, "Request not found.", NotFound: true);
                var booking = await db.PetBookings.FirstOrDefaultAsync(b => b.RequestId == request.Id && b.SitterProfileId == profile.Id && b.Status != BookingStatus.Cancelled && b.Status != BookingStatus.Rejected, ct);
                if (form.Decision == "accept" && offer?.Status == OfferStatus.Accepted && booking is not null) return new(true, "This request has already been accepted.", "accepted", booking.Id);
                if (form.Decision == "decline" && offer?.Status == OfferStatus.Rejected) return new(true, "This request has already been declined.", "declined");
                if (offer is not null && offer.Status != OfferStatus.Pending) return new(false, "A decision has already been saved for this request. Refresh to see its current status.");
                if (!request.Customer!.IsActive || request.Status != RequestStatus.Open || request.StartsAtUtc <= now) return new(false, "This request is no longer available. Your inbox has been refreshed.");
                if (!request.RowVersion.SequenceEqual(version[..8]) || !(offer?.RowVersion ?? new byte[8]).SequenceEqual(version[8..])) return new(false, "The owner updated this request. Review the latest details before responding.");
                if (!SitterRequestPolicy.InServiceArea(request, profile.City, profile.Services.Select(s => s.ServiceType))) return new(false, "This request is outside your current service city or services.");
                if (form.Decision == "accept")
                {
                    var activeBookings = await db.PetBookings.Where(b => b.SitterProfileId == profile.Id && b.StartsAtUtc < request.EndsAtUtc && b.EndsAtUtc > request.StartsAtUtc &&
                        (b.Status == BookingStatus.Pending || b.Status == BookingStatus.AwaitingPayment || b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.InProgress)).ToListAsync(ct);
                    var block = SitterRequestPolicy.AcceptanceBlock(request, profile, activeBookings, now);
                    if (block is not null) return new(false, block);
                }
                if (offer is null)
                {
                    offer = new PetCareOffer { RequestId = request.Id, SitterProfileId = profile.Id, Amount = request.Budget, Currency = request.Currency, CreatedAtUtc = now };
                    db.PetCareOffers.Add(offer);
                }
                var accept = form.Decision == "accept";
                offer.Status = accept ? OfferStatus.Accepted : OfferStatus.Rejected;
                offer.DecidedAtUtc = now;
                if (accept)
                {
                    request.Status = RequestStatus.Booked;
                    booking = new PetBooking { RequestId = request.Id, OfferId = offer.Id, CustomerId = request.CustomerId.Value, SitterProfileId = profile.Id,
                        StartsAtUtc = request.StartsAtUtc, EndsAtUtc = request.EndsAtUtc, TotalAmount = offer.Amount, Currency = offer.Currency,
                        Status = offer.Amount > 0 ? BookingStatus.AwaitingPayment : BookingStatus.Confirmed, CreatedAtUtc = now, CorrelationId = request.CorrelationId };
                    db.PetBookings.Add(booking);
                }
                db.Notifications.Add(new Notification { Category = NotificationCategory.Request, UserId = request.CustomerId.Value,
                    Title = $"{account.FirstName} {(accept ? "accepted" : "declined")} your request",
                    Body = $"{request.Title} · {DashboardViewModel.Service(request.ServiceType)} · {(accept ? "Your booking is accepted." : "Your request is still open to other sitters.")}",
                    RelativeUrl = accept ? "/Dashboard/Customer#bookings" : "/Dashboard/Customer#requests", CreatedAtUtc = now });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new(true, accept ? $"Decision sent — {request.Customer.FirstName} was notified and the booking moved to Accepted." : $"Decision sent — {request.Customer.FirstName} was notified that you declined this request.", accept ? "accepted" : "declined", booking?.Id);
            });
        }
        catch (DbUpdateConcurrencyException) { return new(false, "This request changed while you were responding. Review the refreshed inbox and try again."); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(false, "This request has already been processed. Your inbox has been refreshed."); }
    }
}
