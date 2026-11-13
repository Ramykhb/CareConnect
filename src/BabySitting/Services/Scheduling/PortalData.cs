using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;

namespace BabySitting.Services.Scheduling;

public sealed record PortalData(BabysitterProfile? Profile, int Unread, int Incoming)
{
    public static async Task<PortalData> LoadAsync(BabySittingDbContext db, ApplicationUser account, DateTimeOffset now, CancellationToken ct)
    {
        var profile = await db.BabysitterProfiles.AsNoTracking().Include(p => p.Services).Include(p => p.Availability).Include(p => p.ScheduleExceptions).AsSplitQuery().SingleOrDefaultAsync(p => p.UserId == account.Id, ct);
        var city = profile?.City ?? account.City;
        var services = profile?.Services.Select(s => s.ServiceType).ToArray() ?? [];
        var incoming = await db.ChildcareRequests.CountAsync(r => r.Status == RequestStatus.Open && r.StartsAtUtc > now && r.CustomerId != null && r.CustomerId != account.Id && r.Customer!.IsActive && city != null && r.City == city && (services.Length == 0 || services.Contains(r.ServiceType)) && !r.Offers.Any(o => o.SitterProfile.UserId == account.Id), ct);
        return new(profile, await db.Notifications.CountAsync(n => n.UserId == account.Id && n.ReadAtUtc == null, ct), incoming);
    }
}
