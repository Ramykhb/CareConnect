using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.ViewModels;

namespace PetSitting.Services.Scheduling;

public interface ISitterAvailabilityService
{
    Task<SitterAvailabilityViewModel> LoadAsync(ApplicationUser account, DateOnly? date, string? view, CancellationToken ct = default);
    Task<ScheduleWriteResult> SaveAsync(Guid userId, AvailabilityEditForm form, CancellationToken ct = default);
    Task<ScheduleWriteResult> ToggleAsync(Guid userId, bool accepting, string version, CancellationToken ct = default);
    Task<int> AffectedAsync(Guid userId, AvailabilityEditForm form, CancellationToken ct = default);
}

public sealed class SitterAvailabilityService(PetSittingDbContext db, TimeProvider clock) : ISitterAvailabilityService
{
    public async Task<SitterAvailabilityViewModel> LoadAsync(ApplicationUser account, DateOnly? date, string? view, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow(); var selected = date is { Year: >= 1901 and <= 2099 } ? date.Value : ScheduleMath.LocalDate(now);
        var portal = await PortalData.LoadAsync(db, account, now, ct);
        var mode = view == "week" ? "week" : "month";
        var start = mode == "week" ? ScheduleMath.Monday(selected) : ScheduleMath.Monday(new(selected.Year, selected.Month, 1));
        var end = mode == "week" ? start.AddDays(7) : new DateOnly(selected.Year, selected.Month, 1).AddMonths(1).AddDays(7);
        var from = ScheduleMath.Utc(start); var to = ScheduleMath.Utc(end);
        var bookings = await db.PetBookings.AsNoTracking().Where(b => b.SitterProfile.UserId == account.Id && b.StartsAtUtc < to && b.EndsAtUtc > from && b.Status != BookingStatus.Cancelled && b.Status != BookingStatus.Rejected)
            .Include(b => b.Payments).Include(b => b.Customer).Include(b => b.Request).ThenInclude(r => r.Pets).ThenInclude(p => p.Pet).AsSplitQuery().ToListAsync(ct);
        var pending = await db.PetCareOffers.AsNoTracking().Where(o => o.SitterProfile.UserId == account.Id && o.Status == OfferStatus.Pending && o.Request.Status == RequestStatus.Open && o.Request.StartsAtUtc >= now && o.Request.StartsAtUtc < to && o.Request.EndsAtUtc > from)
            .Include(o => o.Request).ThenInclude(r => r.Customer).Include(o => o.Request).ThenInclude(r => r.Pets).ThenInclude(p => p.Pet).AsSplitQuery().ToListAsync(ct);
        var events = bookings.Select(b => new CalendarEventViewModel(b.Id, SitterBookingsViewModel.PetNames(b) + " · " + DashboardViewModel.Service(b.Request.ServiceType), b.Customer.FirstName + " " + b.Customer.LastName, b.StartsAtUtc, b.EndsAtUtc,
            b.Status == BookingStatus.InProgress ? "in-progress" : b.Status == BookingStatus.Pending || b.Status == BookingStatus.AwaitingPayment && !PetSitting.Services.Bookings.BookingRules.FullyPaid(b) ? "pending" : b.Status == BookingStatus.Completed ? "completed" : "booked"))
            .Concat(pending.Where(o => !bookings.Any(b => b.RequestId == o.RequestId)).Select(o => new CalendarEventViewModel(o.RequestId, "Request pending · " + string.Join(", ", o.Request.Pets.Select(p => p.Pet.Name)), o.Request.Customer?.FirstName ?? "Owner", o.Request.StartsAtUtc, o.Request.EndsAtUtc, "pending", true))).ToArray();
        var available = portal.Profile is not null && portal.Profile.IsAvailable && ScheduleMath.Covered(portal.Profile.Availability, portal.Profile.ScheduleExceptions, ScheduleMath.Utc(selected), ScheduleMath.Utc(selected.AddDays(1)));
        var rule = portal.Profile?.ScheduleExceptions.Where(e => ScheduleMath.Occurrences(e, ScheduleMath.Utc(selected), ScheduleMath.Utc(selected.AddDays(1))).Any()).OrderByDescending(e => e.CreatedAtUtc).FirstOrDefault();
        return new() { Account = account, Profile = portal.Profile, Unread = portal.Unread, IncomingRequestCount = portal.Incoming, Now = now, SelectedDate = selected, View = mode, Events = events,
            Editor = new() { Version = portal.Profile is null ? "" : Convert.ToBase64String(portal.Profile.RowVersion), StartDate = selected, EndDate = selected, Available = available, Reason = rule?.Reason, View = mode }, ActiveBookings = bookings };
    }
    public async Task<int> AffectedAsync(Guid userId, AvailabilityEditForm form, CancellationToken ct = default)
    {
        var profile = await db.PetSitterProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null || form.Available) return 0;
        var rule = form.ToException(profile.Id, clock.GetUtcNow());
        var bookings = await db.PetBookings.AsNoTracking().Where(b => b.SitterProfileId == profile.Id && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.AwaitingPayment || b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.InProgress)).ToListAsync(ct);
        return bookings.Count(b => ScheduleMath.Occurrences(rule, b.StartsAtUtc, b.EndsAtUtc).Any());
    }
    public async Task<ScheduleWriteResult> SaveAsync(Guid userId, AvailabilityEditForm form, CancellationToken ct = default)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(form, new(form), errors, true)) return new(false, errors.First().ErrorMessage!);
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var profile = await db.PetSitterProfiles.Include(p => p.ScheduleExceptions).SingleOrDefaultAsync(p => p.UserId == userId, ct);
                if (profile is null) return new ScheduleWriteResult(false, "A sitter profile is required before setting availability.");
                if (!Matches(profile.RowVersion, form.Version)) return new(false, "Your schedule changed. Review the latest calendar before saving.");
                var last = profile.ScheduleExceptions.Select(e => e.CreatedAtUtc).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
                var now = clock.GetUtcNow(); if (now <= last) now = last.AddTicks(1);
                var rule = form.ToException(profile.Id, now);
                // Reject an explicitly selected nonexistent local time before any write.
                if (!form.AllDay) { _ = ScheduleMath.Utc(form.StartDate, form.StartTime); _ = ScheduleMath.Utc(form.ApplyRange ? form.EndDate : form.StartDate, form.EndTime); }
                _ = ScheduleMath.Occurrences(rule, ScheduleMath.Utc(form.StartDate), ScheduleMath.Utc((form.ApplyRange ? form.EndDate : form.StartDate).AddDays(1))).ToArray();
                var bookings = await db.PetBookings.Where(b => b.SitterProfileId == profile.Id && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.AwaitingPayment || b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.InProgress)).ToListAsync(ct);
                if (!rule.IsAvailable && bookings.Any(b => ScheduleMath.Occurrences(rule, b.StartsAtUtc, b.EndsAtUtc).Any())) return new(false, "These dates affect an active booking. Keep its booked hours available or resolve the booking first.");
                profile.ScheduleRevision = Guid.NewGuid();
                db.SitterScheduleExceptions.Add(rule);
                await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
                return new(true, "Availability saved. Your private reason is visible only to you.");
            });
        }
        catch (DbUpdateConcurrencyException) { return new(false, "Your schedule changed. Refresh and try again."); }
        catch (ArgumentException ex) { return new(false, ex.Message); }
    }
    public async Task<ScheduleWriteResult> ToggleAsync(Guid userId, bool accepting, string version, CancellationToken ct = default)
    {
        var profile = await db.PetSitterProfiles.SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null) return new(false, "A sitter profile is required before accepting requests.");
        if (!Matches(profile.RowVersion, version)) return new(false, "Your schedule changed. Refresh before changing your availability.");
        profile.IsAvailable = accepting; profile.ScheduleRevision = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); return new(true, accepting ? "You’re now accepting requests." : "New requests are paused. Existing bookings are unchanged."); }
        catch (DbUpdateConcurrencyException) { return new(false, "Your schedule changed. Refresh and try again."); }
    }
    private static bool Matches(byte[] actual, string version) { try { return actual.Length == 8 && actual.SequenceEqual(Convert.FromBase64String(version ?? "")); } catch (FormatException) { return false; } }
}
