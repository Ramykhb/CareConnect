using BabySitting.Models;
using BabySitting.Services.Bookings;

namespace BabySitting.ViewModels;

public sealed class ParentBookingItem
{
    public ChildcareRequest Request { get; init; } = null!;
    public BabysittingBooking? Booking { get; init; }
    public BabysitterProfile? Sitter { get; init; }
    public DateTimeOffset Now { get; init; }
    public string Reference => "CC-" + Request.Id.ToString("N")[..8].ToUpperInvariant();
    public string ChildrenSummary => DashboardViewModel.Children(Request);
    public string SitterName => Sitter is null ? "Awaiting a sitter" : Sitter.User.FirstName + " " + Sitter.User.LastName;
    public decimal Amount => Booking?.TotalAmount ?? Request.Budget;
    public string Currency => Booking?.Currency ?? Request.Currency;
    public string Bucket => Request.Status == RequestStatus.Cancelled ? "cancelled" : Booking?.Status switch
    {
        BookingStatus.Completed => "completed", BookingStatus.InProgress => "progress", BookingStatus.Confirmed or BookingStatus.AwaitingPayment => "accepted",
        BookingStatus.Rejected => "rejected", BookingStatus.Cancelled when Request.Status != RequestStatus.Open => "cancelled",
        _ => Request.Status == RequestStatus.Completed ? "completed" : Request.Status == RequestStatus.Expired || Request.StartsAtUtc <= Now ? "expired"
            : Request.Offers.Count > 0 && Request.Offers.All(o => o.Status is OfferStatus.Rejected or OfferStatus.Withdrawn) ? "rejected" : "pending"
    };
    public string Status => Bucket switch
    {
        "completed" => "Completed", "progress" => "In progress", "accepted" => Booking is not null && !BookingRules.FullyPaid(Booking) ? "Payment pending" : "Accepted",
        "rejected" => "Rejected", "cancelled" => "Cancelled", "expired" => "Expired", _ => "Pending"
    };
    public string Css => Bucket switch { "completed" => "completed", "progress" => "progress", "accepted" => Status == "Payment pending" ? "pending" : "accepted", "rejected" => "rejected", "pending" => "pending", _ => "muted" };
}

public sealed class ParentBookingsViewModel
{
    public IReadOnlyList<ParentBookingItem> Items { get; init; } = [];
    public string Filter { get; set; } = "all";
    public string? Search { get; set; }
    // Kept for URL compatibility with the pet version; a parent has one family, so it is never used to filter.
    public Guid? PetId { get; set; }
    public ServiceType? Service { get; set; }
    public int Days { get; set; }
    public int Page { get; set; } = 1;
    public const int PageSize = 8;
    public static readonly (string Key, string Label)[] Tabs = [("all", "All"), ("pending", "Pending"), ("accepted", "Accepted"), ("progress", "In progress"), ("completed", "Completed"), ("rejected", "Rejected"), ("cancelled", "Cancelled"), ("expired", "Expired")];
    public IEnumerable<ParentBookingItem> Filtered => Items.Where(i => Filter == "all" || i.Bucket == Filter);
    public int Total => Filtered.Count();
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
    public int CurrentPage => Math.Clamp(Page, 1, Pages);
    public IEnumerable<ParentBookingItem> Rows => Filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize);
    public int Count(string key) => key == "all" ? Items.Count : Items.Count(i => i.Bucket == key);
}

public sealed class ParentBookingDetails
{
    public ParentBookingItem Item { get; init; } = null!;
    public ChildcareRequest Request => Item.Request;
    public BabysittingBooking? Booking => Item.Booking;
    public BabysitterProfile? Sitter => Item.Sitter;
    public ParentRequestForm? Draft { get; init; }
    public double? AverageRating { get; init; }
    public int ReviewCount { get; init; }
    public bool CanCancel => ParentBookingService.CanCancel(Request, Booking, Item.Now);
    public bool CanPay => Booking is not null && ParentBookingService.CanPay(Booking);
    public bool CanReview => Booking is { Status: BookingStatus.Completed, Review: null };
    public bool HasReceipt => Booking is not null && Booking.Payments.Any(p => p.Status == PaymentStatus.Succeeded && p.Currency == Booking.Currency);
    public decimal Paid => Booking is null ? 0 : BookingRules.Paid(Booking);
    public decimal Refunded => Booking?.Payments.Where(p => p.Status == PaymentStatus.Refunded && p.Currency == Booking.Currency).Sum(p => p.Amount) ?? 0;
    public string Version => Convert.ToBase64String(Booking?.RowVersion ?? Request.RowVersion);
}
