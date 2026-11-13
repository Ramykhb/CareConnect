using System.ComponentModel.DataAnnotations;
using System.Globalization;
using BabySitting.Models;

namespace BabySitting.ViewModels;

public interface IPortalViewModel
{
    ApplicationUser Account { get; }
    BabysitterProfile? Profile { get; }
    int Unread { get; }
    int IncomingRequestCount { get; }
}

public enum IncomingRequestState { New, Pending, Accepted, Declined, Expired }

public sealed class SitterRequestsViewModel : IPortalViewModel
{
    public ApplicationUser Account { get; init; } = new();
    public BabysitterProfile? Profile { get; init; }
    public int Unread { get; init; }
    public DateTimeOffset Now { get; init; }
    public IReadOnlyList<IncomingRequestViewModel> Items { get; init; } = [];
    public string Filter { get; set; } = "new";
    public string? Search { get; set; }
    public Guid? SelectedId { get; set; }
    public string? Confirmation { get; set; }
    public int IncomingRequestCount => Items.Count(i => i.State == IncomingRequestState.New);
    public IEnumerable<IncomingRequestViewModel> VisibleItems => Items.Where(i => i.Bucket == Filter && (string.IsNullOrWhiteSpace(Search) || i.SearchText.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)));
    public IncomingRequestViewModel? Selected => VisibleItems.FirstOrDefault(i => i.Id == SelectedId) ?? VisibleItems.FirstOrDefault();
    public int Count(IncomingRequestState state) => Items.Count(i => i.State == state);
    public static string NormalizeFilter(string? filter) => filter is "pending" or "accepted" or "declined" or "expired" ? filter : "new";
    public string RelativeTime(DateTimeOffset value)
    {
        var elapsed = Now - value;
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} h ago";
        if (elapsed.TotalDays < 2) return "Yesterday";
        return DashboardViewModel.Local(value).ToString("d MMM", CultureInfo.InvariantCulture);
    }
}

public sealed class IncomingRequestViewModel
{
    public Guid Id { get; init; }
    public string Version { get; init; } = "";
    public string Title { get; init; } = "";
    public string OwnerName { get; init; } = "";
    public string OwnerFirstName { get; init; } = "";
    public int OwnerSince { get; init; }
    public ServiceType ServiceType { get; init; }
    public string Service => DashboardViewModel.Service(ServiceType);
    public string City { get; init; } = "";
    public string? Address { get; init; }
    public string Note { get; init; } = "";
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public DateTimeOffset StartsAtUtc { get; init; }
    public DateTimeOffset EndsAtUtc { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public IncomingRequestState State { get; init; }
    public string Bucket => State.ToString().ToLowerInvariant();
    public string StateLabel => State == IncomingRequestState.Pending ? "Pending decision" : State.ToString();
    public string StateClass => State switch { IncomingRequestState.Accepted => "green", IncomingRequestState.New => "purple", IncomingRequestState.Pending => "yellow", _ => "gray" };
    public IncomingChildrenViewModel Children { get; init; } = new(1, 0, 0, null, null, null);
    public IReadOnlyList<RequestFitCheck> FitChecks { get; init; } = [];
    public bool CanAccept { get; init; }
    public bool CanDecline { get; init; }
    public string? BlockReason { get; init; }
    public Guid? BookingId { get; init; }
    public BookingStatus? BookingStatus { get; init; }
    public DateTimeOffset? AcceptedAtUtc { get; init; }
    public string ChildrenSummary => DashboardViewModel.Children(Children.Count, Children.Youngest, Children.Oldest);
    public string SearchText => $"{OwnerName} {ChildrenSummary} {Title} {Service} {City}";
    public string DateSummary => $"{DashboardViewModel.Local(StartsAtUtc).ToString("d MMM", CultureInfo.InvariantCulture)}–{DashboardViewModel.Local(EndsAtUtc).ToString("d MMM", CultureInfo.InvariantCulture)}";
    public string Duration => ServiceType is ServiceType.OvernightCare
        ? $"{Math.Max(1, (int)Math.Ceiling((EndsAtUtc - StartsAtUtc).TotalDays))} nights"
        : $"{(EndsAtUtc - StartsAtUtc).TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} hours";
}

// Restricted care details from the request and family profile; no names, birth dates, or schools.
public sealed record IncomingChildrenViewModel(int Count, int Youngest, int Oldest, string? HealthNotes, string? Routines, string? CareInstructions);
public sealed record RequestFitCheck(string Text, bool? Fits);

public sealed class RequestDecisionForm
{
    [Required] public Guid RequestId { get; set; }
    [Required, RegularExpression("^(accept|decline)$")] public string Decision { get; set; } = "";
    [Required, MaxLength(64)] public string Version { get; set; } = "";
}

public sealed record RequestDecisionResult(bool Succeeded, string Message, string Filter = "new", Guid? BookingId = null, bool NotFound = false);
