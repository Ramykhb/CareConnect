using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;

namespace PetSitting.Services.Accounts;

public sealed record ClosureResult(bool Succeeded, string Message, bool NotFound = false);
public sealed record ClosureRecords(int Bookings, int ActiveBookings, int Requests, int Payments, int Reviews);
public sealed record ClosureItem(AccountClosureRequest Request, string Role, ClosureRecords Records);

public interface IAccountClosureService
{
    Task<AccountClosureRequest?> PendingForUserAsync(Guid userId, CancellationToken ct = default);
    Task<ClosureResult> RequestAsync(Guid userId, string? reason, CancellationToken ct = default);
    Task<ClosureResult> WithdrawAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<ClosureItem>> ListAsync(ClosureStatus? status, CancellationToken ct = default);
    Task<ClosureItem?> GetAsync(Guid id, CancellationToken ct = default);
    Task<ClosureResult> ApproveAsync(Guid managerId, Guid id, string? note, string? version, CancellationToken ct = default);
    Task<ClosureResult> RejectAsync(Guid managerId, Guid id, string? note, string? version, CancellationToken ct = default);
}

public sealed class AccountClosureService(PetSittingDbContext db, UserManager<ApplicationUser> users, TimeProvider clock) : IAccountClosureService
{
    private static readonly BookingStatus[] Active = [BookingStatus.Pending, BookingStatus.AwaitingPayment, BookingStatus.Confirmed, BookingStatus.InProgress];

    public Task<AccountClosureRequest?> PendingForUserAsync(Guid userId, CancellationToken ct = default) =>
        db.AccountClosureRequests.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId && r.Status == ClosureStatus.Pending, ct);

    public async Task<ClosureResult> RequestAsync(Guid userId, string? reason, CancellationToken ct = default)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason?.Length > 1000) return new(false, "Keep the reason under 1,000 characters.");
        if (await PendingForUserAsync(userId, ct) is not null) return new(true, "Your closure request is already with a platform manager.");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        if (user is null) return new(false, "Account not found.", true);
        var now = clock.GetUtcNow();
        var request = new AccountClosureRequest { UserId = userId, Reason = reason, CreatedAtUtc = now };
        db.AccountClosureRequests.Add(request);
        // Every platform manager is told (live, through the notification feed).
        foreach (var manager in await users.GetUsersInRoleAsync(Roles.PlatformManager))
            db.Notifications.Add(new Notification { UserId = manager.Id, Category = NotificationCategory.General, SourceKey = "closure:" + request.Id,
                Title = "Account closure requested", Body = $"{user.FirstName} {user.LastName} asked to close their account.", RelativeUrl = "/AccountClosures/Details/" + request.Id, CreatedAtUtc = now });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(true, "Your closure request is already with a platform manager."); }
        return new(true, "Your closure request was sent. A platform manager will review it; you can withdraw it until then.");
    }

    public async Task<ClosureResult> WithdrawAsync(Guid userId, CancellationToken ct = default)
    {
        var request = await db.AccountClosureRequests.SingleOrDefaultAsync(r => r.UserId == userId && r.Status == ClosureStatus.Pending, ct);
        if (request is null) return new(false, "There is no pending closure request to withdraw.");
        request.Status = ClosureStatus.Withdrawn; request.DecidedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new(true, "Your closure request was withdrawn.");
    }

    public async Task<IReadOnlyList<ClosureItem>> ListAsync(ClosureStatus? status, CancellationToken ct = default)
    {
        var rows = await db.AccountClosureRequests.AsNoTracking().Include(r => r.User)
            .Where(r => status == null || r.Status == status).OrderBy(r => r.Status).ThenByDescending(r => r.CreatedAtUtc).Take(200).ToListAsync(ct);
        var result = new List<ClosureItem>();
        foreach (var r in rows) result.Add(new(r, await RoleAsync(r.User), await RecordsAsync(r.UserId, ct)));
        return result;
    }

    public async Task<ClosureItem?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var r = await db.AccountClosureRequests.AsNoTracking().Include(x => x.User).SingleOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? null : new(r, await RoleAsync(r.User), await RecordsAsync(r.UserId, ct));
    }

    public async Task<ClosureResult> ApproveAsync(Guid managerId, Guid id, string? note, string? version, CancellationToken ct = default)
    {
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > 1000) return new(false, "Keep the note under 1,000 characters.");
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var request = await db.AccountClosureRequests.SingleOrDefaultAsync(r => r.Id == id, ct);
                if (request is null) return new ClosureResult(false, "Request not found.", true);
                if (request.Status != ClosureStatus.Pending) return new(false, "This request was already decided.");
                if (!Matches(request.RowVersion, version)) return new(false, "This request changed. Review the latest details and try again.");
                if (request.UserId == managerId) return new(false, "Another platform manager must review your own closure request.");
                var records = await RecordsAsync(request.UserId, ct);
                if (records.ActiveBookings > 0) return new(false, $"This account still has {records.ActiveBookings} active booking(s). They must be completed or cancelled before the account can be closed.");
                var user = await users.FindByIdAsync(request.UserId.ToString());
                if (user is null) return new(false, "Account not found.", true);
                var now = clock.GetUtcNow();

                // Keep care history, remove what identifies the person, and make sign-in impossible.
                var placeholder = $"closed-{user.Id:N}@closed.invalid";
                user.IsActive = false;
                user.FirstName = "Closed"; user.LastName = "account";
                user.City = null; user.PhoneNumber = null; user.PhoneNumberConfirmed = false;
                user.HomeAddress = null; user.EmergencyContact = null; user.ProfilePhotoVersion = null;
                user.TwoFactorEnabled = false; user.LockoutEnabled = true; user.LockoutEnd = DateTimeOffset.MaxValue;
                await users.SetUserNameAsync(user, placeholder);
                await users.SetEmailAsync(user, placeholder);
                user.EmailConfirmed = false;
                var update = await users.UpdateAsync(user);
                if (!update.Succeeded) return new(false, string.Join(" ", update.Errors.Select(e => e.Description)));
                await users.UpdateSecurityStampAsync(user); // signs the person out everywhere
                await db.AccountPhotos.Where(p => p.UserId == user.Id).ExecuteDeleteAsync(ct);
                await db.SitterVerifications.Where(v => v.SitterProfile.UserId == user.Id).ExecuteDeleteAsync(ct);
                var openRequests = await db.PetCareRequests.Where(r => r.CustomerId == user.Id && r.Status == RequestStatus.Open).ToListAsync(ct);
                foreach (var openRequest in openRequests) openRequest.Status = RequestStatus.Cancelled;
                await db.PetSitterProfiles.Where(p => p.UserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsAvailable, false), ct);

                request.Status = ClosureStatus.Approved; request.DecidedAtUtc = now; request.DecidedById = managerId; request.ManagerNote = note;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new(true, "The account was closed. Care records were kept and personal details were removed.");
            });
        }
        catch (DbUpdateConcurrencyException) { return new(false, "This request changed. Refresh and try again."); }
    }

    public async Task<ClosureResult> RejectAsync(Guid managerId, Guid id, string? note, string? version, CancellationToken ct = default)
    {
        note = note?.Trim();
        if (string.IsNullOrEmpty(note) || note.Length > 1000) return new(false, "Explain the decision to the user (up to 1,000 characters).");
        var request = await db.AccountClosureRequests.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return new(false, "Request not found.", true);
        if (request.Status != ClosureStatus.Pending) return new(false, "This request was already decided.");
        if (!Matches(request.RowVersion, version)) return new(false, "This request changed. Review the latest details and try again.");
        var now = clock.GetUtcNow();
        request.Status = ClosureStatus.Rejected; request.DecidedAtUtc = now; request.DecidedById = managerId; request.ManagerNote = note;
        db.Notifications.Add(new Notification { UserId = request.UserId, Category = NotificationCategory.General, SourceKey = "closure-decision:" + request.Id,
            Title = "Your account closure request was declined", Body = note, RelativeUrl = "/Identity/Account/Manage/DeletePersonalData", CreatedAtUtc = now });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return new(false, "This request changed. Refresh and try again."); }
        return new(true, "The request was declined and the user was notified.");
    }

    private async Task<string> RoleAsync(ApplicationUser user) =>
        (await users.GetRolesAsync(user)).FirstOrDefault() switch { Roles.Customer => "Customer", Roles.Sitter => "Sitter", Roles.PlatformManager => "Platform manager", _ => "Account" };

    private async Task<ClosureRecords> RecordsAsync(Guid userId, CancellationToken ct)
    {
        var bookings = db.PetBookings.Where(b => b.CustomerId == userId || b.SitterProfile.UserId == userId);
        return new(await bookings.CountAsync(ct), await bookings.CountAsync(b => Active.Contains(b.Status), ct),
            await db.PetCareRequests.CountAsync(r => r.CustomerId == userId, ct),
            await db.Payments.CountAsync(p => p.PayerId == userId || p.Booking.SitterProfile.UserId == userId, ct),
            await db.Reviews.CountAsync(r => r.ReviewerId == userId || r.Booking.SitterProfile.UserId == userId, ct));
    }

    private static bool Matches(byte[] actual, string? version)
    {
        try { return actual.Length == 8 && actual.SequenceEqual(Convert.FromBase64String(version ?? "")); } catch (FormatException) { return false; }
    }
}
