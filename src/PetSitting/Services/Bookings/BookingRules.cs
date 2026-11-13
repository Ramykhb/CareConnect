using System.Security.Cryptography;
using System.Text;
using PetSitting.Models;
using PetSitting.ViewModels;

namespace PetSitting.Services.Bookings;

public static class BookingRules
{
    public static decimal Paid(PetBooking booking) => booking.Payments.Where(p => p.Status == PaymentStatus.Succeeded && p.Currency == booking.Currency).Sum(p => p.Amount);
    public static bool FullyPaid(PetBooking booking) => booking.TotalAmount == 0 || Paid(booking) >= booking.TotalAmount;
    public static string Label(PetBooking booking) => booking.Status == BookingStatus.AwaitingPayment ? "Accepted" : DashboardViewModel.Status(booking.Status);
    public static string Css(PetBooking booking) => booking.Status == BookingStatus.AwaitingPayment ? "green" : DashboardViewModel.StatusClass(booking.Status);
    public static string? StartBlock(PetBooking booking, DateTimeOffset now) => booking.Status is not (BookingStatus.Confirmed or BookingStatus.AwaitingPayment) ? "Only an accepted booking can be started." : !FullyPaid(booking) ? "Payment must succeed in full before care can start." : now < booking.StartsAtUtc ? "Care can start at the scheduled start time." : null;
    public static bool CanUpdateCare(PetBooking booking) => booking.Status == BookingStatus.InProgress;
    public static IEnumerable<CareTaskViewModel> Tasks(PetBooking booking)
    {
        foreach (var pet in booking.Request.Pets.Select(p => p.Pet))
        foreach (var (kind, note) in new[] { ("Medical notes", pet.MedicalNotes), ("Feeding", pet.FeedingInstructions), ("Special care", pet.CareInstructions) })
        {
            if (string.IsNullOrWhiteSpace(note)) continue;
            var key = pet.Id.ToString("N") + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + note)))[..24];
            var saved = booking.CareTasks.SingleOrDefault(t => t.Key == key);
            yield return new(key, kind + " · " + pet.Name, note, saved?.CompletedAtUtc is not null, saved?.DueAtUtc);
        }
        foreach (var task in booking.CareTasks.Where(t => t.Key.StartsWith("custom:", StringComparison.Ordinal)).OrderBy(t => t.DueAtUtc ?? DateTimeOffset.MaxValue).ThenBy(t => t.CreatedAtUtc))
            yield return new(task.Key, task.Label, null, task.CompletedAtUtc is not null, task.DueAtUtc);
    }
}
