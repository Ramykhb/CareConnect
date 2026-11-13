using System.ComponentModel.DataAnnotations;
using PetSitting.Models;
using PetSitting.Services.Bookings;
using PetSitting.Services.Scheduling;

namespace PetSitting.ViewModels;

public sealed class SitterBookingsViewModel : IPortalViewModel
{
    public ApplicationUser Account { get; init; } = new();
    public PetSitterProfile? Profile { get; init; }
    public int Unread { get; init; }
    public int IncomingRequestCount { get; init; }
    public DateTimeOffset Now { get; init; }
    public IReadOnlyList<PetBooking> Bookings { get; init; } = [];
    public PetBooking? Booking { get; init; }
    public string Filter { get; set; } = "upcoming";
    public string? Search { get; set; }
    public Guid UpdateToken { get; } = Guid.NewGuid();
    public Guid IssueToken { get; } = Guid.NewGuid();
    public DateOnly Today => ScheduleMath.LocalDate(Now);
    public DateTimeOffset WeekStart => ScheduleMath.Utc(ScheduleMath.Monday(Today));
    public DateTimeOffset WeekEnd => ScheduleMath.Utc(ScheduleMath.Monday(Today).AddDays(7));
    public IEnumerable<PetBooking> ThisWeek => Bookings.Where(b => b.StartsAtUtc < WeekEnd && b.EndsAtUtc > WeekStart && b.Status is not (BookingStatus.Cancelled or BookingStatus.Rejected));
    public IEnumerable<PetBooking> Visible => Bookings.Where(b => Matches(b, Filter) && (string.IsNullOrWhiteSpace(Search) || $"{b.Request.Title} {PetNames(b)} {b.Customer.FirstName} {b.Customer.LastName} {DashboardViewModel.Service(b.Request.ServiceType)}".Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)))
        .OrderBy(b => Filter is "completed" or "cancelled" ? -b.StartsAtUtc.UtcTicks : b.StartsAtUtc.UtcTicks);
    public int Count(string filter) => Bookings.Count(b => Matches(b, filter));
    public static string Normalize(string? filter) => filter is "in-progress" or "accepted" or "completed" or "cancelled" ? filter : "upcoming";
    public static bool Matches(PetBooking b, string filter) => filter switch { "in-progress" => b.Status == BookingStatus.InProgress, "accepted" => b.Status is BookingStatus.Confirmed or BookingStatus.AwaitingPayment, "completed" => b.Status == BookingStatus.Completed, "cancelled" => b.Status is BookingStatus.Cancelled or BookingStatus.Rejected, _ => DashboardViewModel.IsActive(b.Status) };
    public static string PetNames(PetBooking b) => b.Request.Pets.Count == 0 ? b.Request.Title : string.Join(", ", b.Request.Pets.Select(p => p.Pet.Name));
    public static string Reference(PetBooking b) => "PP-" + b.Id.ToString("N")[..8].ToUpperInvariant();
    public double WeekHours => ThisWeek.Sum(b => (Min(b.EndsAtUtc, WeekEnd) - Max(b.StartsAtUtc, WeekStart)).TotalHours);
    public string WeekEarnings => string.Join(" · ", ThisWeek.GroupBy(b => b.Currency).Select(g => DashboardViewModel.Money(g.Sum(b => b.TotalAmount), g.Key)));
    public string Group(PetBooking b) => b.StartsAtUtc < ScheduleMath.Utc(Today.AddDays(1)) && b.EndsAtUtc > ScheduleMath.Utc(Today) ? "Today · " + Today.ToString("ddd d MMM") : b.StartsAtUtc >= WeekStart && b.StartsAtUtc < WeekEnd ? "This week" : b.StartsAtUtc > Now ? "Later" : "Earlier";
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

public sealed class BookingTransitionForm
{
    public Guid Id { get; set; }
    [Required, MaxLength(32)] public string Version { get; set; } = "";
    [Required, RegularExpression("^(start|complete)$")] public string Action { get; set; } = "";
}
public sealed class CareTaskForm
{
    public Guid Id { get; set; }
    [Required, MaxLength(80)] public string Key { get; set; } = "";
    public bool Completed { get; set; }
}
public sealed class AddCareTaskForm
{
    public Guid Id { get; set; }
    [Required, MaxLength(200)] public string Label { get; set; } = "";
    public DateTime? DueAtLocal { get; set; }
}
public sealed class BookingUpdateForm
{
    public Guid Id { get; set; }
    public Guid ClientToken { get; set; }
    [MaxLength(2000)] public string? Message { get; set; }
    public IFormFile? Photo { get; set; }
}
public sealed record BookingWriteResult(bool Succeeded, string Message, bool NotFound = false);
public sealed record CareTaskViewModel(string Key, string Label, string? Note, bool Completed, DateTimeOffset? DueAtUtc);
