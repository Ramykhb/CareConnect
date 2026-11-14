using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.ViewModels;

namespace PetSitting.Services.Dashboard;

public interface IDashboardService
{
    Task<DashboardViewModel> LoadAsync(ApplicationUser account, bool sitter, CancellationToken ct = default);
}

public sealed class DashboardService(PetSittingDbContext db, TimeProvider clock) : IDashboardService
{
    public async Task<DashboardViewModel> LoadAsync(ApplicationUser account, bool sitter, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var profile = sitter ? await db.PetSitterProfiles.AsNoTracking().Include(p => p.Services).SingleOrDefaultAsync(p => p.UserId == account.Id, ct) : null;
        var ownBookings = db.PetBookings.AsNoTracking().Where(b => sitter ? b.SitterProfile.UserId == account.Id : b.CustomerId == account.Id);
        // Keep all current-year activity and future bookings; totals below include all history.
        var january = new DateTime(DashboardViewModel.Local(now).Year, 1, 1);
        var yearStart = new DateTimeOffset(january, TimeZoneInfo.FindSystemTimeZoneById("Asia/Beirut").GetUtcOffset(january)).ToUniversalTime();
        var bookings = await ownBookings.Where(b => b.EndsAtUtc >= yearStart || b.Payments.Any(p => p.PaidAtUtc >= yearStart || p.CreatedAtUtc >= yearStart))
            .Include(b => b.Request).ThenInclude(r => r.Pets).ThenInclude(p => p.Pet)
            .Include(b => b.Customer).Include(b => b.SitterProfile).ThenInclude(p => p.User)
            .Include(b => b.Payments).Include(b => b.Updates).AsSplitQuery().ToListAsync(ct);
        var city = profile?.City ?? account.City;
        var services = profile?.Services.Select(s => s.ServiceType).ToArray() ?? [];
        var requests = db.PetCareRequests.AsNoTracking().Where(r => r.Status == RequestStatus.Open && r.StartsAtUtc > now);
        requests = sitter
            ? requests.Where(r => r.CustomerId != null && r.Customer!.IsActive && r.CustomerId != account.Id && city != null && r.City == city &&
                (services.Length == 0 || services.Contains(r.ServiceType)) && !r.Offers.Any(o => o.SitterProfile.UserId == account.Id))
            : requests.Where(r => r.CustomerId == account.Id);
        var offers = db.PetCareOffers.Where(o => o.SitterProfile.UserId == account.Id);
        var notifications = db.Notifications.AsNoTracking().Where(n => n.UserId == account.Id);
        if (!sitter)
        {
            var preferences = await db.AlertPreferences.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == account.Id, ct) ?? new();
            notifications = notifications.Where(n => (n.Category != NotificationCategory.Request || preferences.Requests)
                && (n.Category != NotificationCategory.Booking || preferences.Bookings)
                && (n.Category != NotificationCategory.Payment || preferences.Payments)
                && (n.Category != NotificationCategory.Review || preferences.Reviews));
        }
        return new DashboardViewModel
        {
            Account = account, IsSitter = sitter, Profile = profile, Now = now, Bookings = bookings,
            Pets = sitter ? [] : await db.Pets.AsNoTracking().Where(p => p.OwnerId == account.Id).OrderBy(p => p.Name).ToListAsync(ct),
            Requests = await requests.Include(r => r.Customer).Include(r => r.Pets).ThenInclude(p => p.Pet).OrderBy(r => r.StartsAtUtc).ToListAsync(ct),
            Notifications = await notifications.OrderByDescending(n => n.CreatedAtUtc).Take(10).ToListAsync(ct),
            Unread = await notifications.CountAsync(n => n.ReadAtUtc == null, ct),
            Completed = await ownBookings.CountAsync(b => b.Status == BookingStatus.Completed, ct),
            Cancelled = await ownBookings.CountAsync(b => b.Status == BookingStatus.Cancelled, ct),
            AcceptedOffers = sitter ? await offers.CountAsync(o => o.Status == OfferStatus.Accepted, ct) : 0,
            DeclinedOffers = sitter ? await offers.CountAsync(o => o.Status == OfferStatus.Rejected, ct) : 0,
            Sitters = sitter ? [] : await db.PetSitterProfiles.AsNoTracking().Include(p => p.User)
                .Where(p => p.IsAvailable && p.User.IsActive && p.UserId != account.Id && city != null && p.City == city)
                .OrderByDescending(p => p.VerificationStatus == VerificationStatus.Verified).ThenByDescending(p => p.YearsOfExperience).Take(3).ToListAsync(ct)
        };
    }
}
