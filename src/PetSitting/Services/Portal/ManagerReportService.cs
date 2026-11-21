using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;

namespace PetSitting.Services.Portal;

public sealed record ReportCount(string Label, int Count);
public sealed record ReportMonth(string Label, int Bookings, decimal Revenue);
public sealed record ReportSitter(string Name, int Completed, decimal Earned, double? Rating);

// Everything a platform manager sees on the overview page, for the chosen period (the last N days up to now).
public sealed record ManagerReport(int Days, DateTimeOffset From, DateTimeOffset To,
    int Customers, int Sitters, int Managers, int SwitchedOff, int NewAccounts,
    int NewRequests, IReadOnlyList<ReportCount> RequestsByStatus,
    int NewBookings, IReadOnlyList<ReportCount> BookingsByStatus, int CompletedBookings, int CancelledBookings,
    decimal Revenue, decimal Refunded, int PaymentsFailed, decimal PayoutsSent, decimal PayoutsWaiting, int PayoutsWaitingCount,
    int Reviews, double? AverageRating,
    int ClosuresWaiting, int MessagesWaiting, int MessagesRetrying, int CombinedLinks,
    IReadOnlyList<ReportMonth> Months, IReadOnlyList<ReportSitter> TopSitters)
{
    public int VerificationsWaiting { get; init; }
    public double CompletionRate => NewBookings == 0 ? 0 : Math.Round(CompletedBookings * 100.0 / NewBookings, 1);
    public double CancellationRate => NewBookings == 0 ? 0 : Math.Round(CancelledBookings * 100.0 / NewBookings, 1);
}

public interface IManagerReportService
{
    Task<ManagerReport> ReportAsync(int days, CancellationToken ct = default);
    string ToCsv(ManagerReport report);
}

public sealed class ManagerReportService(PetSittingDbContext db, TimeProvider clock) : IManagerReportService
{
    public static readonly int[] Periods = [7, 30, 90, 365];
    public const int MonthsShown = 6;

    public static int Normalize(int? days) => days is { } d && Periods.Contains(d) ? d : 30;

    public async Task<ManagerReport> ReportAsync(int days, CancellationToken ct = default)
    {
        days = Normalize(days);
        var now = clock.GetUtcNow();
        var from = now.AddDays(-days);

        // People: one row per account and role.
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id
                           group ur by r.Name into g select new { Role = g.Key, Count = g.Count() }).ToListAsync(ct);
        int RoleCount(string role) => roles.FirstOrDefault(r => r.Role == role)?.Count ?? 0;
        var switchedOff = await db.Users.CountAsync(u => !u.IsActive, ct);
        var newAccounts = await db.Users.CountAsync(u => u.CreatedAtUtc >= from && u.CreatedAtUtc <= now, ct);

        // Requests and bookings created in the period, by status.
        var requests = await db.PetCareRequests.Where(r => r.CreatedAtUtc >= from && r.CreatedAtUtc <= now).GroupBy(r => r.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var bookings = await db.PetBookings.Where(b => b.CreatedAtUtc >= from && b.CreatedAtUtc <= now).GroupBy(b => b.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Booked(BookingStatus s) => bookings.FirstOrDefault(b => b.Key == s)?.Count ?? 0;

        // This report is denominated in USD. Keep other currencies out of all monetary totals;
        // activity counts still include every currency, with no implicit conversion.
        var revenue = await db.Payments.Where(p => p.Currency == "USD" && p.Status == PaymentStatus.Succeeded && p.PaidAtUtc >= from && p.PaidAtUtc <= now).SumAsync(p => (decimal?)p.Amount, ct) ?? 0;
        var refunded = await db.Payments.Where(p => p.Currency == "USD" && p.Status == PaymentStatus.Refunded && (p.PaidAtUtc ?? p.CreatedAtUtc) >= from && (p.PaidAtUtc ?? p.CreatedAtUtc) <= now).SumAsync(p => (decimal?)p.Amount, ct) ?? 0;
        var failed = await db.Payments.CountAsync(p => p.Status == PaymentStatus.Failed && p.CreatedAtUtc >= from && p.CreatedAtUtc <= now, ct);
        var payoutsSent = await db.SitterPayouts.Where(p => p.Currency == "USD" && p.Status == PayoutStatus.Paid && p.PaidAtUtc >= from && p.PaidAtUtc <= now).SumAsync(p => (decimal?)p.Amount, ct) ?? 0;
        var waiting = await db.SitterPayouts.Where(p => p.Currency == "USD" && p.Status == PayoutStatus.Requested)
            .GroupBy(p => 1).Select(g => new { Count = g.Count(), Total = g.Sum(p => p.Amount) }).FirstOrDefaultAsync(ct);

        var reviews = await db.Reviews.Where(r => r.CreatedAtUtc >= from && r.CreatedAtUtc <= now)
            .GroupBy(r => 1).Select(g => new { Count = g.Count(), Average = g.Average(r => (double)r.Rating) }).FirstOrDefaultAsync(ct);

        // Things that need a manager or show the system is healthy.
        var closures = await db.AccountClosureRequests.CountAsync(c => c.Status == ClosureStatus.Pending, ct);
        var messagesWaiting = await db.MessageOutboxEntries.CountAsync(m => m.PublishedAtUtc == null, ct);
        var messagesRetrying = await db.MessageOutboxEntries.CountAsync(m => m.PublishedAtUtc == null && m.Attempts > 0, ct);
        var verifications = await db.SitterVerifications.CountAsync(v => v.Status == VerificationStatus.Pending, ct);
        var links = await db.CombinedCareLinks.CountAsync(l => l.LocalRequestId != null && l.PeerRequestId != null, ct);

        // The last six calendar months (UTC): new bookings and money received.
        var firstMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-(MonthsShown - 1));
        var bookingDates = await db.PetBookings.Where(b => b.CreatedAtUtc >= firstMonth && b.CreatedAtUtc <= now).Select(b => b.CreatedAtUtc).ToListAsync(ct);
        var paid = await db.Payments.Where(p => p.Currency == "USD" && p.Status == PaymentStatus.Succeeded && p.PaidAtUtc >= firstMonth && p.PaidAtUtc <= now)
            .Select(p => new { p.PaidAtUtc, p.Amount }).ToListAsync(ct);
        var months = Enumerable.Range(0, MonthsShown).Select(i => firstMonth.AddMonths(i)).Select(m => new ReportMonth(
            m.ToString("MMM yyyy", CultureInfo.InvariantCulture),
            bookingDates.Count(d => d.UtcDateTime.Year == m.Year && d.UtcDateTime.Month == m.Month),
            paid.Where(p => p.PaidAtUtc!.Value.UtcDateTime.Year == m.Year && p.PaidAtUtc.Value.UtcDateTime.Month == m.Month).Sum(p => p.Amount))).ToList();

        // Sitters with the most completed bookings in the period.
        var top = await db.PetBookings.Where(b => b.Status == BookingStatus.Completed && b.CompletedAtUtc >= from && b.CompletedAtUtc <= now)
            .GroupBy(b => b.SitterProfileId).Select(g => new { Id = g.Key, Count = g.Count(), Earned = g.Sum(b => b.Currency == "USD" ? b.TotalAmount : 0) })
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.Earned).Take(5).ToListAsync(ct);
        var ids = top.Select(t => t.Id).ToList();
        var names = await db.PetSitterProfiles.Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, Name = p.User.FirstName + " " + p.User.LastName }).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var ratings = await db.Reviews.Where(r => ids.Contains(r.Booking.SitterProfileId)).GroupBy(r => r.Booking.SitterProfileId)
            .Select(g => new { g.Key, Average = g.Average(r => (double)r.Rating) }).ToDictionaryAsync(r => r.Key, r => r.Average, ct);
        var topSitters = top.Select(t => new ReportSitter(names.GetValueOrDefault(t.Id, "Sitter"), t.Count, t.Earned,
            ratings.TryGetValue(t.Id, out var rating) ? Math.Round(rating, 1) : null)).ToList();

        return new ManagerReport(days, from, now,
            RoleCount(Roles.Customer), RoleCount(Roles.Sitter), RoleCount(Roles.PlatformManager), switchedOff, newAccounts,
            requests.Sum(r => r.Count), Enum.GetValues<RequestStatus>().Select(s => new ReportCount(s.ToString(), requests.FirstOrDefault(r => r.Key == s)?.Count ?? 0)).ToList(),
            bookings.Sum(b => b.Count), Enum.GetValues<BookingStatus>().Select(s => new ReportCount(s.ToString(), Booked(s))).ToList(),
            Booked(BookingStatus.Completed), Booked(BookingStatus.Cancelled),
            revenue, refunded, failed, payoutsSent, waiting?.Total ?? 0, waiting?.Count ?? 0,
            reviews?.Count ?? 0, reviews is null ? null : Math.Round(reviews.Average, 1),
            closures, messagesWaiting, messagesRetrying, links, months, topSitters) { VerificationsWaiting = verifications };
    }

    // A simple CSV a manager can open in Excel. Names are user input, so they are quoted and kept from starting a formula.
    public string ToCsv(ManagerReport r)
    {
        var csv = new StringBuilder();
        void Row(params object?[] cells) => csv.AppendLine(string.Join(",", cells.Select(Cell)));
        Row("CareConnect PetSitting report", $"last {r.Days} days", r.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " to " + r.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Row();
        Row("Metric", "Value");
        Row("Pet owners", r.Customers); Row("Pet sitters", r.Sitters); Row("Managers", r.Managers);
        Row("Switched-off accounts", r.SwitchedOff); Row("New accounts", r.NewAccounts);
        Row("New requests", r.NewRequests); Row("New bookings", r.NewBookings);
        Row("Completed bookings", r.CompletedBookings); Row("Cancelled bookings", r.CancelledBookings);
        Row("Completion rate %", r.CompletionRate); Row("Cancellation rate %", r.CancellationRate);
        Row("Money received (USD)", r.Revenue); Row("Refunded (USD)", r.Refunded); Row("Failed payments", r.PaymentsFailed);
        Row("Payouts sent (USD)", r.PayoutsSent); Row("Payouts waiting (USD)", r.PayoutsWaiting);
        Row("Reviews", r.Reviews); Row("Average rating", r.AverageRating);
        Row();
        Row("Requests by status", "Count"); foreach (var s in r.RequestsByStatus) Row(s.Label, s.Count);
        Row();
        Row("Bookings by status", "Count"); foreach (var s in r.BookingsByStatus) Row(s.Label, s.Count);
        Row();
        Row("Month", "New bookings", "Money received (USD)"); foreach (var m in r.Months) Row(m.Label, m.Bookings, m.Revenue);
        Row();
        Row("Top sitter", "Completed bookings", "Earned (USD)", "Average rating");
        foreach (var s in r.TopSitters) Row(s.Name, s.Completed, s.Earned, s.Rating);
        return csv.ToString();
    }

    private static string Cell(object? value)
    {
        var text = value switch { null => "", decimal d => d.ToString("0.00", CultureInfo.InvariantCulture), double d => d.ToString("0.#", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
        if (value is string && text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 || value is string ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
