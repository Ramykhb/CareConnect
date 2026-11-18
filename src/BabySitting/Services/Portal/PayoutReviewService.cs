using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.ViewModels;

namespace BabySitting.Services.Portal;

public sealed record PayoutItem(SitterPayout Payout, string SitterName, string? SitterEmail);

// Platform managers confirm that a requested payout was sent (no bank is connected; this records it).
public interface IPayoutReviewService
{
    Task<IReadOnlyList<PayoutItem>> ListAsync(PayoutStatus? status, CancellationToken ct = default);
    Task<PortalWriteResult> MarkPaidAsync(Guid payoutId, string? version, CancellationToken ct = default);
}

public sealed class PayoutReviewService(BabySittingDbContext db, TimeProvider clock) : IPayoutReviewService
{
    public async Task<IReadOnlyList<PayoutItem>> ListAsync(PayoutStatus? status, CancellationToken ct = default) =>
        await db.SitterPayouts.AsNoTracking().Where(p => status == null || p.Status == status)
            .OrderBy(p => p.Status).ThenByDescending(p => p.CreatedAtUtc).Take(200)
            .Select(p => new PayoutItem(p, p.SitterProfile.User.FirstName + " " + p.SitterProfile.User.LastName, p.SitterProfile.User.Email))
            .ToListAsync(ct);

    public async Task<PortalWriteResult> MarkPaidAsync(Guid payoutId, string? version, CancellationToken ct = default)
    {
        var payout = await db.SitterPayouts.Include(p => p.SitterProfile).SingleOrDefaultAsync(p => p.Id == payoutId, ct);
        if (payout is null) return new(false, "Payout request not found.", true);
        if (payout.Status == PayoutStatus.Paid) return new(true, "This payout is already marked as paid.");
        if (payout.Status != PayoutStatus.Requested) return new(false, "Only a pending payout request can be marked as paid.");
        if (!Matches(payout.RowVersion, version)) return new(false, "This payout changed. Refresh the list and try again.");
        var now = clock.GetUtcNow();
        payout.Status = PayoutStatus.Paid; payout.PaidAtUtc = now;
        db.Notifications.Add(new Notification { UserId = payout.SitterProfile.UserId, Category = NotificationCategory.Payment, SourceKey = "payout-paid:" + payout.Id,
            Title = "Payout sent · " + DashboardViewModel.Money(payout.Amount, payout.Currency), Body = "Your payout request was marked as paid.", RelativeUrl = "/Sitter/Earnings", CreatedAtUtc = now });
        try { await db.SaveChangesAsync(ct); return new(true, "Payout marked as paid. The sitter was notified."); }
        catch (DbUpdateConcurrencyException) { return new(false, "This payout changed (the sitter may have cancelled it). Refresh and try again."); }
    }

    private static bool Matches(byte[] actual, string? value) { try { return actual.Length == 8 && actual.SequenceEqual(Convert.FromBase64String(value ?? "")); } catch (FormatException) { return false; } }
}
