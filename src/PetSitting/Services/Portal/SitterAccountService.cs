using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Scheduling;
using PetSitting.ViewModels;

namespace PetSitting.Services.Portal;

public interface ISitterAccountService
{
    Task<SitterReviewsViewModel> ReviewsAsync(ApplicationUser account, CancellationToken ct = default);
    Task<PortalWriteResult> ReplyAsync(Guid userId, ReviewReplyForm form, CancellationToken ct = default);
    Task<SitterSettingsViewModel> SettingsAsync(ApplicationUser account, CancellationToken ct = default);
    Task<PortalWriteResult> SaveSettingsAsync(Guid userId, SitterSettingsForm form, CancellationToken ct = default);
    Task<SitterNotificationsViewModel> NotificationsAsync(ApplicationUser account, CancellationToken ct = default);
    Task<PortalWriteResult> ReadAsync(Guid userId, Guid? id, CancellationToken ct = default);
    Task<PortalWriteResult> SaveAlertsAsync(Guid userId, AlertPreferencesForm form, CancellationToken ct = default);
}

public sealed class SitterAccountService(PetSittingDbContext db, UserManager<ApplicationUser> users, TimeProvider clock) : ISitterAccountService
{
    public async Task<SitterReviewsViewModel> ReviewsAsync(ApplicationUser account, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow(); var portal = await PortalData.LoadAsync(db, account, now, ct);
        var reviews = await db.Reviews.AsNoTracking().Where(r => r.Booking.SitterProfile.UserId == account.Id && r.Booking.CustomerId == r.ReviewerId && r.Booking.Status == BookingStatus.Completed)
            .Include(r => r.Reviewer).Include(r => r.Booking).ThenInclude(b => b.Request).ThenInclude(r => r.Pets).ThenInclude(p => p.Pet).AsSplitQuery().OrderByDescending(r => r.CreatedAtUtc).ToListAsync(ct);
        return new() { Account = account, Profile = portal.Profile, Now = now, Unread = portal.Unread, IncomingRequestCount = portal.Incoming, Reviews = reviews };
    }
    public async Task<PortalWriteResult> ReplyAsync(Guid userId, ReviewReplyForm form, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(form.Reply) || form.Reply.Length > 2000) return new(false, "Enter a reply of up to 2,000 characters.");
        var review = await db.Reviews.Include(r => r.Booking).SingleOrDefaultAsync(r => r.Id == form.Id && r.Booking.SitterProfile.UserId == userId && r.ReviewerId == r.Booking.CustomerId && r.Booking.Status == BookingStatus.Completed, ct);
        if (review is null) return new(false, "Review not found.", true);
        if (!Matches(review.RowVersion, form.Version)) return new(false, "This review changed. Refresh before saving your reply.");
        review.SitterReply = form.Reply.Trim(); review.RepliedAtUtc = clock.GetUtcNow();
        try { await db.SaveChangesAsync(ct); return new(true, "Your reply has been saved."); } catch (DbUpdateConcurrencyException) { return new(false, "The review changed. Refresh and try again."); }
    }
    public async Task<SitterSettingsViewModel> SettingsAsync(ApplicationUser account, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow(); var portal = await PortalData.LoadAsync(db, account, now, ct); var p = portal.Profile;
        var preferences = await db.AlertPreferences.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == account.Id, ct) ?? new();
        return new() { Account = account, Profile = p, Now = now, Unread = portal.Unread, IncomingRequestCount = portal.Incoming,
            Form = new() { FirstName = account.FirstName, LastName = account.LastName, Phone = account.PhoneNumber, City = p?.City ?? account.City ?? "", Bio = p?.Bio ?? "", Experience = p?.YearsOfExperience ?? 0, HourlyRate = p?.HourlyRate ?? 0, Currency = p?.Currency ?? "USD", Available = p?.IsAvailable ?? true, Services = p is null || p.Services.Count == 0 ? Enum.GetValues<ServiceType>() : p.Services.Select(s => s.ServiceType).ToArray(), UserVersion = account.ConcurrencyStamp, ProfileVersion = p is null ? "" : Convert.ToBase64String(p.RowVersion) },
            Alerts = new() { Version = preferences.RowVersion.Length == 0 ? "" : Convert.ToBase64String(preferences.RowVersion), Requests = preferences.Requests, Bookings = preferences.Bookings, Payments = preferences.Payments, Reviews = preferences.Reviews, RequestSound = preferences.RequestSound } };
    }
    public async Task<PortalWriteResult> SaveSettingsAsync(Guid userId, SitterSettingsForm form, CancellationToken ct = default)
    {
        var errors = new List<ValidationResult>(); if (!Validator.TryValidateObject(form, new(form), errors, true)) return new(false, errors.First().ErrorMessage!);
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var account = await users.FindByIdAsync(userId.ToString()); if (account is null) return new PortalWriteResult(false, "Account not found.", true);
                var profile = await db.PetSitterProfiles.Include(p => p.Services).SingleOrDefaultAsync(p => p.UserId == userId, ct);
                if ((account.ConcurrencyStamp ?? "") != (form.UserVersion ?? "") || (profile is null ? !string.IsNullOrEmpty(form.ProfileVersion) : !Matches(profile.RowVersion, form.ProfileVersion))) return new(false, "Your account changed. Refresh settings before saving.");
                account.FirstName = form.FirstName.Trim(); account.LastName = form.LastName.Trim(); account.City = form.City.Trim();
                if (account.PhoneNumber != form.Phone?.Trim()) account.PhoneNumberConfirmed = false;
                account.PhoneNumber = string.IsNullOrWhiteSpace(form.Phone) ? null : form.Phone.Trim();
                if (profile is null) { profile = new() { UserId = userId }; db.PetSitterProfiles.Add(profile); }
                profile.City = form.City.Trim(); profile.Bio = form.Bio.Trim(); profile.HourlyRate = form.HourlyRate; profile.Currency = form.Currency; profile.YearsOfExperience = form.Experience; profile.IsAvailable = form.Available;
                var chosen = form.Services.Distinct().ToHashSet();
                db.SitterServices.RemoveRange(profile.Services.Where(s => !chosen.Contains(s.ServiceType)));
                foreach (var type in chosen.Where(t => !profile.Services.Any(s => s.ServiceType == t))) db.SitterServices.Add(new() { SitterProfileId = profile.Id, ServiceType = type });
                var update = await users.UpdateAsync(account); if (!update.Succeeded) return new(false, string.Join(" ", update.Errors.Select(e => e.Description)));
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true, "Your account and sitter profile have been updated.");
            });
        }
        catch (DbUpdateConcurrencyException) { return new(false, "Your settings changed. Refresh and try again."); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(false, "Your profile changed. Refresh settings before saving."); }
    }
    public async Task<PortalWriteResult> SaveAlertsAsync(Guid userId, AlertPreferencesForm form, CancellationToken ct = default)
    {
        var p = await db.AlertPreferences.SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (p is null) { if (!string.IsNullOrEmpty(form.Version)) return new(false, "Refresh your alert preferences before saving."); p = new() { UserId = userId }; db.AlertPreferences.Add(p); }
        else if (!Matches(p.RowVersion, form.Version)) return new(false, "Your alert preferences changed. Refresh before saving.");
        p.Requests = form.Requests; p.Bookings = form.Bookings; p.Payments = form.Payments; p.Reviews = form.Reviews; p.RequestSound = form.RequestSound;
        try { await db.SaveChangesAsync(ct); return new(true, "Your in-app alert preferences have been saved."); }
        catch (DbUpdateConcurrencyException) { return new(false, "Your preferences changed. Refresh and try again."); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(false, "Preferences already exist. Refresh and try again."); }
    }
    public async Task<PortalWriteResult> ReadAsync(Guid userId, Guid? id, CancellationToken ct = default)
    {
        var query = db.Notifications.Where(n => n.UserId == userId);
        if (id is not null && !await query.AnyAsync(n => n.Id == id, ct)) return new(false, "Notification not found.", true);
        await query.Where(n => n.ReadAtUtc == null && (id == null || n.Id == id)).ExecuteUpdateAsync(setters => setters.SetProperty(n => n.ReadAtUtc, clock.GetUtcNow()), ct);
        return new(true, id is null ? "All notifications marked as read." : "Notification marked as read.");
    }
    public async Task<SitterNotificationsViewModel> NotificationsAsync(ApplicationUser account, CancellationToken ct = default)
    {
        await SynchronizeAsync(account, ct);
        var now = clock.GetUtcNow(); var portal = await PortalData.LoadAsync(db, account, now, ct);
        return new() { Account = account, Profile = portal.Profile, Now = now, Unread = portal.Unread, IncomingRequestCount = portal.Incoming,
            Items = await db.Notifications.AsNoTracking().Where(n => n.UserId == account.Id).OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id).ToListAsync(ct), Preferences = await db.AlertPreferences.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == account.Id, ct) ?? new() };
    }
    private async Task SynchronizeAsync(ApplicationUser account, CancellationToken ct)
    {
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var now = clock.GetUtcNow(); var portal = await PortalData.LoadAsync(db, account, now, ct); var preferences = await db.AlertPreferences.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == account.Id, ct) ?? new();
            var candidates = new List<Notification>();
            var city = portal.Profile?.City ?? account.City; var types = portal.Profile?.Services.Select(s => s.ServiceType).ToArray() ?? [];
            if (preferences.Requests && portal.Profile?.IsAvailable != false && city is not null)
            {
                var requests = await db.PetCareRequests.AsNoTracking().Where(r => r.CustomerId != null && r.CustomerId != account.Id && r.Customer!.IsActive && r.Status == RequestStatus.Open && r.StartsAtUtc > now && r.City == city && (types.Length == 0 || types.Contains(r.ServiceType)) && !r.Offers.Any(o => o.SitterProfile.UserId == account.Id)).Include(r => r.Customer).ToListAsync(ct);
                foreach (var r in requests) candidates.Add(new() { Category = NotificationCategory.Request, SourceKey = "request:" + r.Id, Title = "New request from " + r.Customer!.FirstName + " " + r.Customer.LastName, Body = DashboardViewModel.Service(r.ServiceType) + " · " + DashboardViewModel.Money(r.Budget, r.Currency) + " offered · " + r.City, RelativeUrl = "/SitterRequests?id=" + r.Id, CreatedAtUtc = r.CreatedAtUtc });
            }
            if (preferences.Payments)
            {
                var payments = await db.Payments.AsNoTracking().Where(p => p.Booking.SitterProfile.UserId == account.Id).ToListAsync(ct);
                foreach (var p in payments) candidates.Add(new() { Category = NotificationCategory.Payment, SourceKey = $"payment:{p.Id}:{p.Status}", Title = p.Status == PaymentStatus.Succeeded ? "Payment received · " + DashboardViewModel.Money(p.Amount, p.Currency) : p.Status == PaymentStatus.Failed ? "Payment failed" : p.Status == PaymentStatus.Refunded ? "Payment refunded" : "Payment pending", Body = "Booking #PP-" + p.BookingId.ToString("N")[..8].ToUpperInvariant() + " · " + DashboardViewModel.Money(p.Amount, p.Currency), RelativeUrl = "/SitterBookings/Details/" + p.BookingId, CreatedAtUtc = p.PaidAtUtc ?? p.CreatedAtUtc });
            }
            if (preferences.Reviews)
            {
                var reviews = await db.Reviews.AsNoTracking().Where(r => r.Booking.SitterProfile.UserId == account.Id && r.Booking.Status == BookingStatus.Completed && r.ReviewerId == r.Booking.CustomerId).Include(r => r.Reviewer).ToListAsync(ct);
                foreach (var r in reviews) candidates.Add(new() { Category = NotificationCategory.Review, SourceKey = "review:" + r.Id, Title = $"New {r.Rating}-star review from {r.Reviewer.FirstName}", Body = r.Comment ?? "Your care received a new rating.", RelativeUrl = "/Sitter/Reviews#review-" + r.Id, CreatedAtUtc = r.CreatedAtUtc });
            }
            if (preferences.Bookings)
            {
                var cancelled = await db.PetBookings.AsNoTracking().Where(b => b.SitterProfile.UserId == account.Id && b.Status == BookingStatus.Cancelled).ToListAsync(ct);
                foreach (var b in cancelled) candidates.Add(new() { Category = NotificationCategory.Booking, SourceKey = "booking-cancelled:" + b.Id, Title = "Booking cancelled", Body = b.CancellationReason ?? "The booking is no longer active.", RelativeUrl = "/SitterBookings/Details/" + b.Id, CreatedAtUtc = b.CancelledAtUtc ?? b.CreatedAtUtc });
            }
            var keys = candidates.Select(c => c.SourceKey).ToArray();
            var existing = (await db.Notifications.Where(n => n.UserId == account.Id && keys.Contains(n.SourceKey)).Select(n => n.SourceKey).ToListAsync(ct)).ToHashSet();
            foreach (var item in candidates.Where(n => !existing.Contains(n.SourceKey))) { item.UserId = account.Id; item.Title = item.Title[..Math.Min(150, item.Title.Length)]; item.Body = item.Body[..Math.Min(2000, item.Body.Length)]; db.Notifications.Add(item); }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        });
    }
    private static bool Matches(byte[] actual, string? value) { try { return actual.Length == 8 && actual.SequenceEqual(Convert.FromBase64String(value ?? "")); } catch (FormatException) { return false; } }
}
