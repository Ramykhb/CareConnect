using System.ComponentModel.DataAnnotations;
using System.Globalization;
using PetSitting.Models;
using PetSitting.Services.Scheduling;

namespace PetSitting.ViewModels;

public abstract class SitterPortalPage : IPortalViewModel
{
    public ApplicationUser Account { get; init; } = new();
    public PetSitterProfile? Profile { get; init; }
    public int Unread { get; init; }
    public int IncomingRequestCount { get; init; }
    public DateTimeOffset Now { get; init; }
}
public sealed class SitterReportViewModel : SitterPortalPage
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public string Currency { get; init; } = "USD";
    public IReadOnlyList<string> Currencies { get; init; } = ["USD"];
    public IReadOnlyList<Payment> Payments { get; init; } = [];
    public IReadOnlyList<PetCareOffer> Offers { get; init; } = [];
    public IReadOnlyList<PetBooking> Bookings { get; init; } = [];
    public IReadOnlyList<Review> Reviews { get; init; } = [];
    public IReadOnlyList<SitterPayout> Payouts { get; init; } = [];
    public decimal AvailableBalance { get; init; }
    public Guid PayoutToken { get; } = Guid.NewGuid();
    public static DateOnly PaidDate(Payment p) => ScheduleMath.LocalDate(p.PaidAtUtc ?? p.CreatedAtUtc);
    public bool Within(DateTimeOffset value) => ScheduleMath.LocalDate(value) >= From && ScheduleMath.LocalDate(value) <= To;
    public IEnumerable<Payment> PeriodPayments => Payments.Where(p => p.Currency == Currency && PaidDate(p) >= From && PaidDate(p) <= To);
    public IEnumerable<PetCareOffer> PeriodOffers => Offers.Where(o => Within(o.CreatedAtUtc));
    public IEnumerable<Review> PeriodReviews => Reviews.Where(r => Within(r.CreatedAtUtc));
    public decimal Gross => PeriodPayments.Where(p => p.Status == PaymentStatus.Succeeded).Sum(p => p.Amount);
    public decimal MonthGross => Payments.Where(p => p.Currency == Currency && p.Status == PaymentStatus.Succeeded && PaidDate(p).Year == From.Year && PaidDate(p).Month == ScheduleMath.LocalDate(Now).Month).Sum(p => p.Amount);
    public decimal PendingPayouts => Payouts.Where(p => p.Currency == Currency && p.Status == PayoutStatus.Requested).Sum(p => p.Amount);
    public decimal PaidOut => Payouts.Where(p => p.Currency == Currency && p.Status == PayoutStatus.Paid).Sum(p => p.Amount);
    public int Accepted => PeriodOffers.Count(o => o.Status == OfferStatus.Accepted);
    public int Rejected => PeriodOffers.Count(o => o.Status == OfferStatus.Rejected);
    public int Completed => Bookings.Count(b => b.Status == BookingStatus.Completed && Within(b.CompletedAtUtc ?? b.EndsAtUtc));
    public decimal? AcceptanceRate => Accepted + Rejected == 0 ? null : Math.Round(Accepted * 100m / (Accepted + Rejected), 1);
    public double? ReplyMinutes => PeriodOffers.Where(o => o.DecidedAtUtc >= o.Request.CreatedAtUtc).Select(o => (double?)(o.DecidedAtUtc!.Value - o.Request.CreatedAtUtc).TotalMinutes).DefaultIfEmpty(null).Average();
    public double? AverageRating => PeriodReviews.Select(r => (double?)r.Rating).DefaultIfEmpty(null).Average();
    public IEnumerable<ReportMonth> Months
    {
        get
        {
            for (var month = new DateOnly(From.Year, From.Month, 1); month <= To; month = month.AddMonths(1))
            {
                var m = month;
                var offers = PeriodOffers.Where(o => ScheduleMath.LocalDate(o.CreatedAtUtc).Year == m.Year && ScheduleMath.LocalDate(o.CreatedAtUtc).Month == m.Month).ToArray();
                yield return new(m, offers.Length, offers.Count(o => o.Status == OfferStatus.Accepted), offers.Count(o => o.Status == OfferStatus.Rejected), Bookings.Count(b => b.Status == BookingStatus.Completed && Within(b.CompletedAtUtc ?? b.EndsAtUtc) && ScheduleMath.LocalDate(b.CompletedAtUtc ?? b.EndsAtUtc).Year == m.Year && ScheduleMath.LocalDate(b.CompletedAtUtc ?? b.EndsAtUtc).Month == m.Month), PeriodPayments.Where(p => p.Status == PaymentStatus.Succeeded && PaidDate(p).Year == m.Year && PaidDate(p).Month == m.Month).Sum(p => p.Amount), Payouts.Where(p => p.Currency == Currency && p.Status == PayoutStatus.Paid && p.PaidAtUtc != null && Within(p.PaidAtUtc.Value) && ScheduleMath.LocalDate(p.PaidAtUtc.Value).Year == m.Year && ScheduleMath.LocalDate(p.PaidAtUtc.Value).Month == m.Month).Sum(p => p.Amount), PeriodReviews.Where(r => ScheduleMath.LocalDate(r.CreatedAtUtc).Year == m.Year && ScheduleMath.LocalDate(r.CreatedAtUtc).Month == m.Month).Select(r => (double?)r.Rating).DefaultIfEmpty(null).Average());
            }
        }
    }
    public static string Money(decimal amount, string currency) => DashboardViewModel.Money(amount, currency);
}
public sealed record ReportMonth(DateOnly Month, int Requests, int Accepted, int Rejected, int Completed, decimal Earnings, decimal PaidOut, double? Rating);
public sealed class PayoutRequestForm : IValidatableObject
{
    public Guid ClientToken { get; set; }
    [Range(typeof(decimal), "0.01", "99999999")] public decimal Amount { get; set; }
    [Required, RegularExpression("^[A-Z]{3}$")] public string Currency { get; set; } = "USD";
    public IEnumerable<ValidationResult> Validate(ValidationContext context) { if (ClientToken == Guid.Empty || decimal.Round(Amount, 2) != Amount) yield return new("Enter a valid payout amount with no more than two decimal places."); }
}
public sealed class SitterReviewsViewModel : SitterPortalPage
{
    public IReadOnlyList<Review> Reviews { get; init; } = [];
    public int? Stars { get; set; }
    public string? Search { get; set; }
    public string Sort { get; set; } = "recent";
    public bool Unanswered { get; set; }
    public double? Average => Reviews.Select(r => (double?)r.Rating).DefaultIfEmpty(null).Average();
    public IEnumerable<Review> Visible => Reviews.Where(r => (Stars is null || r.Rating == Stars) && (!Unanswered || string.IsNullOrEmpty(r.SitterReply)) && (string.IsNullOrWhiteSpace(Search) || $"{r.Reviewer.FirstName} {r.Reviewer.LastName} {r.Comment} {string.Join(" ", r.Booking.Request.Pets.Select(p => p.Pet.Name))}".Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase))).OrderBy(r => Sort == "highest" ? -r.Rating : Sort == "lowest" ? r.Rating : 0).ThenByDescending(r => r.CreatedAtUtc);
}
public sealed class ReviewReplyForm
{
    public Guid Id { get; set; }
    [Required, MaxLength(32)] public string Version { get; set; } = "";
    [Required, MaxLength(2000), RegularExpression(@"[\s\S]*\S[\s\S]*")] public string Reply { get; set; } = "";
}
public sealed class SitterNotificationsViewModel : SitterPortalPage
{
    public IReadOnlyList<Notification> Items { get; init; } = [];
    public AlertPreferences Preferences { get; init; } = new();
    public string Filter { get; set; } = "all";
    public string? Search { get; set; }
    public IEnumerable<Notification> Visible => Items.Where(n => (Filter == "all" || Category(n).ToString().Equals(Filter, StringComparison.OrdinalIgnoreCase)) && (string.IsNullOrWhiteSpace(Search) || (n.Title + " " + n.Body).Contains(Search, StringComparison.OrdinalIgnoreCase)));
    public static NotificationCategory Category(Notification n) => n.Category != NotificationCategory.General ? n.Category : n.Title.Contains("payment", StringComparison.OrdinalIgnoreCase) || n.Title.Contains("payout", StringComparison.OrdinalIgnoreCase) ? NotificationCategory.Payment : n.Title.Contains("review", StringComparison.OrdinalIgnoreCase) ? NotificationCategory.Review : n.Title.Contains("request", StringComparison.OrdinalIgnoreCase) ? NotificationCategory.Request : n.Title.Contains("booking", StringComparison.OrdinalIgnoreCase) || n.Title.Contains("care", StringComparison.OrdinalIgnoreCase) ? NotificationCategory.Booking : NotificationCategory.General;
    public AlertPreferencesForm PreferencesForm => new() { Version = Preferences.RowVersion.Length == 0 ? "" : Convert.ToBase64String(Preferences.RowVersion), Requests = Preferences.Requests, Bookings = Preferences.Bookings, Payments = Preferences.Payments, Reviews = Preferences.Reviews, RequestSound = Preferences.RequestSound };
}
public sealed class AlertPreferencesForm
{
    [MaxLength(32)] public string? Version { get; set; }
    public bool Requests { get; set; }
    public bool Bookings { get; set; }
    public bool Payments { get; set; }
    public bool Reviews { get; set; }
    public bool RequestSound { get; set; }
}
public sealed class SitterSettingsViewModel : SitterPortalPage
{
    public SitterSettingsForm Form { get; set; } = new();
    public AlertPreferencesForm Alerts { get; init; } = new();
}
public sealed class SitterSettingsForm : IValidatableObject
{
    [Required, StringLength(100), RegularExpression(@".*\S.*")] public string FirstName { get; set; } = "";
    [Required, StringLength(100), RegularExpression(@".*\S.*")] public string LastName { get; set; } = "";
    [Phone, StringLength(30)] public string? Phone { get; set; }
    [Required, StringLength(150), RegularExpression(@".*\S.*")] public string City { get; set; } = "";
    [Required, StringLength(2000), RegularExpression(@"[\s\S]*\S[\s\S]*")] public string Bio { get; set; } = "";
    [Range(0, 80)] public int Experience { get; set; }
    [Range(typeof(decimal), "0", "99999999")] public decimal HourlyRate { get; set; }
    [Required, RegularExpression("^[A-Z]{3}$")] public string Currency { get; set; } = "USD";
    public bool Available { get; set; } = true;
    [MinLength(1)] public ServiceType[] Services { get; set; } = [];
    [MaxLength(100)] public string? UserVersion { get; set; }
    [MaxLength(32)] public string? ProfileVersion { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext context) { if (Services is null || Services.Length == 0 || Services.Any(s => !Enum.IsDefined(s))) yield return new("Choose at least one supported service.", [nameof(Services)]); if (decimal.Round(HourlyRate, 2) != HourlyRate) yield return new("Use no more than two decimal places for the hourly rate.", [nameof(HourlyRate)]); }
}
public sealed record PortalWriteResult(bool Succeeded, string Message, bool NotFound = false);
