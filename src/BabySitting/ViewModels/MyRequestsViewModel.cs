using BabySitting.Models;

namespace BabySitting.ViewModels;

// One row of "My requests": the request plus its latest booking, if a sitter accepted.
public sealed record MyRequestItem(ChildcareRequest Request, BabysittingBooking? Booking, DateTimeOffset Now)
{
    public bool IsExpired => Request.Status == RequestStatus.Expired || Request.Status == RequestStatus.Open && Request.StartsAtUtc <= Now;
    public string Bucket => IsExpired ? "past" : Request.Status switch { RequestStatus.Open => "open", RequestStatus.Booked => "booked", RequestStatus.Completed => "past", _ => "cancelled" };
    public string StatusLabel => IsExpired ? "Expired"
        : Request.Status == RequestStatus.Open ? "Waiting for a sitter"
        : Request.Status == RequestStatus.Booked && Booking is not null ? DashboardViewModel.Status(Booking.Status)
        : Request.Status.ToString();
    public string StatusClass => IsExpired ? "gray" : Request.Status switch { RequestStatus.Open => "yellow", RequestStatus.Booked => "green", RequestStatus.Completed => "green", _ => "gray" };
    public string? SitterName => Booking is null ? null : Booking.SitterProfile.User.FirstName + " " + Booking.SitterProfile.User.LastName;
    public int Declines => Request.Offers.Count(o => o.Status is OfferStatus.Rejected or OfferStatus.Withdrawn);
    public string When => $"{DashboardViewModel.Local(Request.StartsAtUtc):ddd d MMM, HH:mm} – {DashboardViewModel.Local(Request.EndsAtUtc):ddd d MMM, HH:mm}";
}

public sealed class MyRequestsViewModel
{
    public static readonly string[] Filters = ["all", "open", "booked", "past", "cancelled"];
    public IReadOnlyList<MyRequestItem> Items { get; init; } = [];
    public string Filter { get; init; } = "all";
    public IEnumerable<MyRequestItem> Visible => Filter == "all" ? Items : Items.Where(i => i.Bucket == Filter);
}
