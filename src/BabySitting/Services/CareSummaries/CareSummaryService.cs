using CareConnect.Ai;
using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Services.Scheduling;

namespace BabySitting.Services.CareSummaries;

public sealed class CareSummaryService(BabySittingDbContext db, CareSummaryEngine engine, TimeProvider clock) : ICareSummaryService
{
    public async Task<CareSummaryResult?> GenerateAsync(Guid customerId, Guid bookingId, DateOnly? date, CancellationToken ct = default)
    {
        // Always authorize against the database before reading records or consulting the shared cache.
        var booking = await db.BabysittingBookings.AsNoTracking().Where(b => b.Id == bookingId && b.CustomerId == customerId && b.Request.CustomerId == customerId && b.Customer.IsActive)
            .Select(b => new { b.Id, b.RequestId, b.Request.ServiceType, b.Status }).SingleOrDefaultAsync(ct);
        if (booking is null) return null;
        var now = clock.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(now, ScheduleMath.Zone);
        if (date > DateOnly.FromDateTime(localNow.DateTime)) throw new AssistantException("Choose today or an earlier date for a care summary.");
        if (date == DateOnly.MinValue) throw new AssistantException("Choose a valid care date.");
        DateTimeOffset? from = date is { } day ? ScheduleMath.Utc(day) : null;
        DateTimeOffset? until = date is { } endDay ? ScheduleMath.Utc(endDay.AddDays(1)) : null;
        var cutoff = until is { } boundary && boundary < now ? boundary : now;
        var tasksQuery = db.BookingCareTasks.AsNoTracking().Where(t => t.BookingId == bookingId && t.CreatedAtUtc <= now && (until == null || t.CreatedAtUtc < until));
        var undated = date is not null ? await tasksQuery.CountAsync(t => t.DueAtUtc == null && (t.CompletedAtUtc == null || t.CompletedAtUtc < from || t.CompletedAtUtc >= until), ct) : 0;
        if (date is not null) tasksQuery = tasksQuery.Where(t => (t.DueAtUtc >= from && t.DueAtUtc < until) || (t.CompletedAtUtc >= from && t.CompletedAtUtc < until && t.CompletedAtUtc <= now));
        // Deliberately exclude saved task Notes, medical profiles, photos, names, addresses, payments and booking chat.
        var tasks = await tasksQuery.OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.Label, t.CreatedAtUtc, t.DueAtUtc, t.CompletedAtUtc }).ToListAsync(ct);
        var updatesQuery = db.BookingUpdates.AsNoTracking().Where(u => u.BookingId == bookingId && u.CreatedAtUtc <= now);
        if (date is not null) updatesQuery = updatesQuery.Where(u => u.CreatedAtUtc >= from && u.CreatedAtUtc < until);
        var updates = await updatesQuery.OrderBy(u => u.CreatedAtUtc).ThenBy(u => u.Id).Select(u => new { u.Id, u.Message, u.CreatedAtUtc, u.IsIssue, u.HasPhoto }).ToListAsync(ct);
        var sources = new List<CareSummarySource>();
        foreach (var t in tasks)
        {
            var completedAt = t.CompletedAtUtc is { } completed && completed <= now && (until is null || completed < until) ? t.CompletedAtUtc : null;
            sources.Add(new("s" + (sources.Count + 1), "task", t.Label, TimeZoneInfo.ConvertTime(t.CreatedAtUtc, ScheduleMath.Zone),
                t.DueAtUtc is { } due ? TimeZoneInfo.ConvertTime(due, ScheduleMath.Zone) : null,
                completedAt is { } done ? TimeZoneInfo.ConvertTime(done, ScheduleMath.Zone) : null, false));
        }
        foreach (var u in updates)
            sources.Add(new("s" + (sources.Count + 1), "update", string.IsNullOrWhiteSpace(u.Message) ? (u.HasPhoto ? "A photo was shared; its contents have not been analyzed." : "An update without written details was recorded.") : u.Message,
                TimeZoneInfo.ConvertTime(u.CreatedAtUtc, ScheduleMath.Zone), null, null, u.IsIssue,
                "/ParentBookings/Details/" + booking.RequestId + "#care-update-" + u.Id));
        return await engine.GenerateAsync(customerId, bookingId, new("childcare", booking.ServiceType.ToString(), booking.Status.ToString(), date, localNow, sources, undated, TimeZoneInfo.ConvertTime(cutoff, ScheduleMath.Zone)), ct);
    }
}
