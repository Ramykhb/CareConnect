using BabySitting.Models;
using BabySitting.Services.Scheduling;
using BabySitting.ViewModels;

namespace BabySitting.Services.Requests;

// Shared by the inbox and the write path so a green fit check cannot bypass a booking rule.
public static class SitterRequestPolicy
{
    public static string Version(ChildcareRequest request, BabysittingOffer? offer) => Convert.ToBase64String(request.RowVersion.Concat(offer?.RowVersion ?? new byte[8]).ToArray());

    public static IncomingRequestState State(ChildcareRequest request, BabysittingOffer? offer, BabysittingBooking? booking, DateTimeOffset now)
    {
        if (offer?.Status is OfferStatus.Rejected or OfferStatus.Withdrawn) return IncomingRequestState.Declined;
        if (offer?.Status == OfferStatus.Accepted && booking is not null && booking.Status is not (BookingStatus.Cancelled or BookingStatus.Rejected)) return IncomingRequestState.Accepted;
        if (request.Status != RequestStatus.Open || request.StartsAtUtc <= now || offer?.Status == OfferStatus.Expired) return IncomingRequestState.Expired;
        return offer?.Status == OfferStatus.Pending ? IncomingRequestState.Pending : IncomingRequestState.New;
    }

    public static bool InServiceArea(ChildcareRequest request, string? city, IEnumerable<ServiceType> services) =>
        !string.IsNullOrWhiteSpace(city) && string.Equals(city, request.City, StringComparison.OrdinalIgnoreCase) && (!services.Any() || services.Contains(request.ServiceType));

    public static bool Overlaps(ChildcareRequest request, IEnumerable<BabysittingBooking> bookings) => bookings.Any(b =>
        b.RequestId != request.Id && DashboardViewModel.IsActive(b.Status) && b.StartsAtUtc < request.EndsAtUtc && b.EndsAtUtc > request.StartsAtUtc);

    public static bool Covered(ChildcareRequest request, IEnumerable<SitterAvailability> availability, IEnumerable<SitterScheduleException>? exceptions = null) =>
        ScheduleMath.Covered(availability, exceptions ?? [], request.StartsAtUtc, request.EndsAtUtc);

    public static string? AcceptanceBlock(ChildcareRequest request, BabysitterProfile? profile, IEnumerable<BabysittingBooking> bookings, DateTimeOffset now)
    {
        if (request.Status != RequestStatus.Open || request.StartsAtUtc <= now) return "This request is no longer available.";
        if (profile is null) return "A sitter profile is required before you can accept a request.";
        if (!profile.IsAvailable) return "Your sitter profile is not currently accepting requests.";
        if (!InServiceArea(request, profile.City, profile.Services.Select(s => s.ServiceType))) return "This request is outside your current service city or services.";
        if (Overlaps(request, bookings)) return "These dates overlap one of your active bookings.";
        if (!Covered(request, profile.Availability, profile.ScheduleExceptions)) return "These dates are outside your saved availability.";
        return null;
    }
}
