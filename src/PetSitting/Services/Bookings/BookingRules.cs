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
    // Before care starts the checklist follows the current notes. When care starts it is copied into the booking
    // (SaveChecklist), so editing a note afterwards can't change, untick or remove items on that booking.
    public static IEnumerable<CareTaskViewModel> Tasks(PetBooking booking)
    {
        if (booking.ChecklistSavedAtUtc is null) return LiveTasks(booking);
        var saved = booking.CareTasks.Where(t => !t.Key.StartsWith("custom:", StringComparison.Ordinal) && t.Note is not null)
            .OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.Label)
            .Select(t => new CareTaskViewModel(t.Key, t.Label, t.Note, t.CompletedAtUtc is not null, t.DueAtUtc));
        return saved.Concat(CustomTasks(booking));
    }

    // Copies the current note-based items into the booking. Safe to call more than once.
    public static void SaveChecklist(PetBooking booking, DateTimeOffset now)
    {
        if (booking.ChecklistSavedAtUtc is not null) return;
        foreach (var item in LiveTasks(booking).Where(t => !t.Key.StartsWith("custom:", StringComparison.Ordinal)))
        {
            var note = item.Note is null ? "" : item.Note.Length > 2000 ? item.Note[..2000] : item.Note;
            var label = item.Label.Length > 200 ? item.Label[..200] : item.Label;
            var row = booking.CareTasks.SingleOrDefault(t => t.Key == item.Key);
            if (row is null) booking.CareTasks.Add(new BookingCareTask { BookingId = booking.Id, Key = item.Key, Label = label, Note = note, CreatedAtUtc = now });
            else { row.Label = label; row.Note = note; }
        }
        booking.ChecklistSavedAtUtc = now;
    }

    private static IEnumerable<CareTaskViewModel> CustomTasks(PetBooking booking) =>
        booking.CareTasks.Where(t => t.Key.StartsWith("custom:", StringComparison.Ordinal)).OrderBy(t => t.DueAtUtc ?? DateTimeOffset.MaxValue).ThenBy(t => t.CreatedAtUtc)
            .Select(task => new CareTaskViewModel(task.Key, task.Label, null, task.CompletedAtUtc is not null, task.DueAtUtc));

    private static IEnumerable<CareTaskViewModel> LiveTasks(PetBooking booking)
    {
        foreach (var pet in booking.Request.Pets.Select(p => p.Pet))
        foreach (var (kind, note) in new[] { ("Medical notes", pet.MedicalNotes), ("Feeding", pet.FeedingInstructions), ("Special care", pet.CareInstructions) })
        {
            if (string.IsNullOrWhiteSpace(note)) continue;
            var key = pet.Id.ToString("N") + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + note)))[..24];
            var saved = booking.CareTasks.SingleOrDefault(t => t.Key == key);
            yield return new(key, kind + " · " + pet.Name, note, saved?.CompletedAtUtc is not null, saved?.DueAtUtc);
        }
        foreach (var task in CustomTasks(booking)) yield return task;
    }
}
