using System.Security.Cryptography;
using System.Text;
using BabySitting.Models;
using BabySitting.ViewModels;

namespace BabySitting.Services.Bookings;

public static class BookingRules
{
    public static decimal Paid(BabysittingBooking booking) => booking.Payments.Where(p => p.Status == PaymentStatus.Succeeded && p.Currency == booking.Currency).Sum(p => p.Amount);
    public static bool FullyPaid(BabysittingBooking booking) => booking.TotalAmount == 0 || Paid(booking) >= booking.TotalAmount;
    public static string Label(BabysittingBooking booking) => booking.Status == BookingStatus.AwaitingPayment ? "Accepted" : DashboardViewModel.Status(booking.Status);
    public static string Css(BabysittingBooking booking) => booking.Status == BookingStatus.AwaitingPayment ? "green" : DashboardViewModel.StatusClass(booking.Status);
    public static string? StartBlock(BabysittingBooking booking, DateTimeOffset now) => booking.Status is not (BookingStatus.Confirmed or BookingStatus.AwaitingPayment) ? "Only an accepted booking can be started." : !FullyPaid(booking) ? "Payment must succeed in full before care can start." : now < booking.StartsAtUtc ? "Care can start at the scheduled start time." : null;
    public static bool CanUpdateCare(BabysittingBooking booking) => booking.Status == BookingStatus.InProgress;
    public static IEnumerable<CareTaskViewModel> Tasks(BabysittingBooking booking)
    {
        // The parent's own notes seed the checklist; no medication doses or care times are invented.
        var family = booking.Request.FamilyProfile;
        var children = DashboardViewModel.Children(booking.Request);
        foreach (var (kind, note) in new[] { ("Health notes", family?.HealthNotes), ("Routines", family?.Routines), ("Care instructions", booking.Request.CareInstructions), ("Care preferences", family?.CarePreferences) })
        {
            if (string.IsNullOrWhiteSpace(note)) continue;
            var key = booking.Request.Id.ToString("N") + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + note)))[..24];
            var saved = booking.CareTasks.SingleOrDefault(t => t.Key == key);
            yield return new(key, kind + " · " + children, note, saved?.CompletedAtUtc is not null, saved?.DueAtUtc);
        }
        foreach (var task in booking.CareTasks.Where(t => t.Key.StartsWith("custom:", StringComparison.Ordinal)).OrderBy(t => t.DueAtUtc ?? DateTimeOffset.MaxValue).ThenBy(t => t.CreatedAtUtc))
            yield return new(task.Key, task.Label, null, task.CompletedAtUtc is not null, task.DueAtUtc);
    }
}
