using System.Globalization;
using BabySitting.Models;

namespace BabySitting.ViewModels;

public sealed class DashboardViewModel : IPortalViewModel
{
    public ApplicationUser Account { get; init; } = new();
    public bool IsSitter { get; init; }
    public BabysitterProfile? Profile { get; init; }
    public DateTimeOffset Now { get; init; }
    public FamilyProfile? Family { get; init; }
    public IReadOnlyList<BabysittingBooking> Bookings { get; init; } = [];
    public IReadOnlyList<ChildcareRequest> Requests { get; init; } = [];
    public IReadOnlyList<Notification> Notifications { get; init; } = [];
    public IReadOnlyList<BabysitterProfile> Sitters { get; init; } = [];
    public int Completed { get; init; }
    public int Cancelled { get; init; }
    public int Unread { get; init; }
    public int AcceptedOffers { get; init; }
    public int DeclinedOffers { get; init; }
    public int IncomingRequestCount => Requests.Count;
    public string FirstName => Account.FirstName;
    public int ReportYear => Local(Now).Year;
    public int ReportMonth => Local(Now).Month;
    public IEnumerable<BabysittingBooking> Upcoming => Bookings.Where(b => IsActive(b.Status) && b.EndsAtUtc > Now).OrderBy(b => b.StartsAtUtc);
    public IEnumerable<BabysittingBooking> Today => Upcoming.Where(b => Local(b.StartsAtUtc).Date <= Local(Now).Date && Local(b.EndsAtUtc).Date >= Local(Now).Date);
    public int InProgress => Upcoming.Count(b => b.Status == BookingStatus.InProgress);
    public int ThisMonth => Bookings.Count(b => Local(b.StartsAtUtc).Year == ReportYear && Local(b.StartsAtUtc).Month == ReportMonth && b.Status is not (BookingStatus.Cancelled or BookingStatus.Rejected));
    public decimal AcceptanceRate => AcceptedOffers + DeclinedOffers == 0 ? 0 : Math.Round(AcceptedOffers * 100m / (AcceptedOffers + DeclinedOffers));
    // Analytics explicitly use USD; individual rows retain their original currency.
    public decimal TotalPaid => Bookings.SelectMany(b => b.Payments).Where(p => p.Status == PaymentStatus.Succeeded && p.Currency == "USD" && Local(p.PaidAtUtc ?? p.CreatedAtUtc).Year == ReportYear).Sum(p => p.Amount);
    public decimal MonthPaid => Bookings.SelectMany(b => b.Payments).Where(p => p.Status == PaymentStatus.Succeeded && p.Currency == "USD" && Local(p.PaidAtUtc ?? p.CreatedAtUtc).Year == ReportYear && Local(p.PaidAtUtc ?? p.CreatedAtUtc).Month == ReportMonth).Sum(p => p.Amount);
    public IEnumerable<DashboardMonth> Months => Enumerable.Range(1, 12).Select(month => new DashboardMonth(month,
        new DateTime(ReportYear, month, 1).ToString("MMM", CultureInfo.InvariantCulture),
        Bookings.Where(b => Local(b.StartsAtUtc).Year == ReportYear && Local(b.StartsAtUtc).Month == month && b.Status is not (BookingStatus.Cancelled or BookingStatus.Rejected)).Sum(b => (b.EndsAtUtc - b.StartsAtUtc).TotalHours),
        Bookings.SelectMany(b => b.Payments).Where(p => p.Status == PaymentStatus.Succeeded && p.Currency == "USD" && Local(p.PaidAtUtc ?? p.CreatedAtUtc).Year == ReportYear && Local(p.PaidAtUtc ?? p.CreatedAtUtc).Month == month).Sum(p => p.Amount)));
    public static bool IsActive(BookingStatus status) => status is BookingStatus.Pending or BookingStatus.AwaitingPayment or BookingStatus.Confirmed or BookingStatus.InProgress;
    public static string Service(ServiceType type) => type switch { ServiceType.AfterSchoolCare => "After-school care", ServiceType.OvernightCare => "Overnight care", _ => "Babysitting" };
    public static string Status(BookingStatus status) => status switch { BookingStatus.InProgress => "In progress", BookingStatus.Confirmed => "Accepted", BookingStatus.AwaitingPayment => "Payment pending", _ => status.ToString() };
    public static string StatusClass(BookingStatus status) => status switch { BookingStatus.InProgress => "blue", BookingStatus.Confirmed or BookingStatus.Completed => "green", BookingStatus.Cancelled or BookingStatus.Rejected => "gray", _ => "yellow" };
    public static string Money(decimal amount, string currency = "USD") => currency == "USD" ? "$" + amount.ToString("#,0.##", CultureInfo.InvariantCulture) : currency + " " + amount.ToString("#,0.##", CultureInfo.InvariantCulture);
    // Age range only: child names, birth dates, and schools are never stored.
    public static string Children(int count, int youngest, int oldest) => $"{count} {(count == 1 ? "child" : "children")} · {(youngest == oldest ? $"age {youngest}" : $"ages {youngest}–{oldest}")}";
    public static string Children(ChildcareRequest r) => Children(r.NumberOfChildren, r.YoungestChildAgeYears, r.OldestChildAgeYears);
    public static DateTimeOffset Local(DateTimeOffset date) => TimeZoneInfo.ConvertTime(date, TimeZoneInfo.FindSystemTimeZoneById("Asia/Beirut"));
}

public sealed record DashboardMonth(int Number, string Label, double Hours, decimal Paid);

public sealed record DashboardMetric(string Title, string Icon, int Value, string Detail, string Theme = "", int Filled = 0, string Section = "overview");
