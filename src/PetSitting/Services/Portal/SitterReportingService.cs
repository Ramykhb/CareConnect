using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Scheduling;
using PetSitting.ViewModels;

namespace PetSitting.Services.Portal;

public interface ISitterReportingService
{
    Task<SitterReportViewModel> LoadAsync(ApplicationUser account, DateOnly from, DateOnly to, string? currency, CancellationToken ct = default);
    Task<PortalWriteResult> RequestPayoutAsync(Guid userId, PayoutRequestForm form, CancellationToken ct = default);
    Task<PortalWriteResult> CancelPayoutAsync(Guid userId, Guid id, CancellationToken ct = default);
}

public sealed class SitterReportingService(PetSittingDbContext db, TimeProvider clock) : ISitterReportingService
{
    public async Task<SitterReportViewModel> LoadAsync(ApplicationUser account, DateOnly from, DateOnly to, string? currency, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow(); var portal = await PortalData.LoadAsync(db, account, now, ct);
        var payments = await db.Payments.AsNoTracking().Where(p => p.Booking.SitterProfile.UserId == account.Id).Include(p => p.Booking).ThenInclude(b => b.Customer).Include(p => p.Booking).ThenInclude(b => b.Request).ThenInclude(r => r.Pets).ThenInclude(p => p.Pet).AsSplitQuery().ToListAsync(ct);
        var offers = await db.PetCareOffers.AsNoTracking().Where(o => o.SitterProfile.UserId == account.Id).Include(o => o.Request).ToListAsync(ct);
        var bookings = await db.PetBookings.AsNoTracking().Where(b => b.SitterProfile.UserId == account.Id).ToListAsync(ct);
        var reviews = await db.Reviews.AsNoTracking().Where(r => r.Booking.SitterProfile.UserId == account.Id && r.ReviewerId == r.Booking.CustomerId && r.Booking.Status == BookingStatus.Completed).ToListAsync(ct);
        var payouts = await db.SitterPayouts.AsNoTracking().Where(p => p.SitterProfile.UserId == account.Id).Include(p => p.Allocations).ThenInclude(a => a.Payment).ThenInclude(p => p.Booking).ThenInclude(b => b.Request).AsSplitQuery().OrderByDescending(p => p.CreatedAtUtc).ToListAsync(ct);
        var currencies = payments.Select(p => p.Currency).Concat(payouts.Select(p => p.Currency)).Append(portal.Profile?.Currency ?? "USD").Distinct().Order().ToArray();
        var selected = currency is { Length: 3 } && currency.All(char.IsAsciiLetterUpper) ? currency : portal.Profile?.Currency ?? "USD";
        if (!currencies.Contains(selected)) currencies = currencies.Append(selected).Order().ToArray();
        return new() { Account = account, Profile = portal.Profile, Now = now, Unread = portal.Unread, IncomingRequestCount = portal.Incoming, From = from, To = to, Currency = selected, Currencies = currencies,
            Payments = payments, Offers = offers, Bookings = bookings, Reviews = reviews, Payouts = payouts, AvailableBalance = Ledger(payments, payouts, selected, now).Sum(p => p.Remaining) };
    }
    public async Task<PortalWriteResult> RequestPayoutAsync(Guid userId, PayoutRequestForm form, CancellationToken ct = default)
    {
        var errors = new List<ValidationResult>(); if (!Validator.TryValidateObject(form, new(form), errors, true)) return new(false, errors.First().ErrorMessage!);
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var profile = await db.PetSitterProfiles.SingleOrDefaultAsync(p => p.UserId == userId, ct); if (profile is null) return new PortalWriteResult(false, "Set up your sitter profile before requesting a payout.");
                var payouts = await db.SitterPayouts.Where(p => p.SitterProfileId == profile.Id).Include(p => p.Allocations).AsSplitQuery().ToListAsync(ct);
                var existing = payouts.SingleOrDefault(p => p.ClientToken == form.ClientToken);
                if (existing is not null) return new(existing.Amount == form.Amount && existing.Currency == form.Currency, "This payout request has already been recorded.");
                var payments = await db.Payments.Where(p => p.Booking.SitterProfileId == profile.Id && p.Currency == form.Currency && p.Status == PaymentStatus.Succeeded).Include(p => p.Booking).ToListAsync(ct);
                var now = clock.GetUtcNow(); var ledger = Ledger(payments, payouts, form.Currency, now);
                if (ledger.Sum(p => p.Remaining) < form.Amount) return new(false, "The amount exceeds your available balance from completed, paid bookings.");
                var payout = new SitterPayout { SitterProfileId = profile.Id, ClientToken = form.ClientToken, Amount = form.Amount, Currency = form.Currency, Status = PayoutStatus.Requested, CreatedAtUtc = now };
                var remaining = form.Amount;
                foreach (var entry in ledger) { var amount = Math.Min(remaining, entry.Remaining); if (amount <= 0) continue; payout.Allocations.Add(new() { PaymentId = entry.Payment.Id, Amount = amount, CreatedAtUtc = now }); remaining -= amount; if (remaining == 0) break; }
                db.SitterPayouts.Add(payout);
                db.Notifications.Add(new() { UserId = userId, Category = NotificationCategory.Payment, SourceKey = "payout:" + payout.Id, Title = "Payout request recorded", Body = DashboardViewModel.Money(form.Amount, form.Currency) + " reserved for your payout request. No bank transfer has been made.", RelativeUrl = "/Sitter/Earnings", CreatedAtUtc = now });
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                return new(true, "Payout request recorded. The amount is reserved; no bank transfer has been made.");
            });
        }
        catch (DbUpdateConcurrencyException) { return new(false, "Your balance changed. Refresh before requesting a payout."); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(false, "This payout request has already been processed. Refresh to see its status."); }
    }
    public async Task<PortalWriteResult> CancelPayoutAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var payout = await db.SitterPayouts.SingleOrDefaultAsync(p => p.Id == id && p.SitterProfile.UserId == userId, ct);
        if (payout is null) return new(false, "Payout request not found.", true);
        if (payout.Status == PayoutStatus.Cancelled) return new(true, "This request is already cancelled.");
        if (payout.Status != PayoutStatus.Requested) return new(false, "Only a pending payout request can be cancelled.");
        payout.Status = PayoutStatus.Cancelled; payout.CancelledAtUtc = clock.GetUtcNow();
        try { await db.SaveChangesAsync(ct); return new(true, "Payout request cancelled. Its reserved amount is available again."); } catch (DbUpdateConcurrencyException) { return new(false, "The payout changed. Refresh and try again."); }
    }
    public static IReadOnlyList<(Payment Payment, decimal Remaining)> Ledger(IEnumerable<Payment> payments, IEnumerable<SitterPayout> payouts, string currency, DateTimeOffset now)
    {
        var reserved = payouts.Where(p => p.Status != PayoutStatus.Cancelled && p.Currency == currency).SelectMany(p => p.Allocations).GroupBy(a => a.PaymentId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var result = new List<(Payment, decimal)>();
        foreach (var group in payments.Where(p => p.Status == PaymentStatus.Succeeded && p.Currency == currency && p.Booking.Currency == currency && p.Booking.Status == BookingStatus.Completed && (p.PaidAtUtc ?? p.CreatedAtUtc) <= now).GroupBy(p => p.BookingId))
        {
            var limit = group.First().Booking.TotalAmount;
            foreach (var payment in group.OrderBy(p => p.PaidAtUtc ?? p.CreatedAtUtc).ThenBy(p => p.Id))
            {
                var eligible = Math.Min(limit, payment.Amount); limit -= eligible;
                var remaining = Math.Max(0, eligible - reserved.GetValueOrDefault(payment.Id));
                if (remaining > 0) result.Add((payment, remaining));
            }
        }
        return result;
    }
}
