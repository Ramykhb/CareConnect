using System.Data;
using System.ComponentModel.DataAnnotations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Scheduling;
using PetSitting.ViewModels;

namespace PetSitting.Services.Bookings;

public interface ISitterBookingService
{
    Task<SitterBookingsViewModel> LoadAsync(ApplicationUser account, Guid? id = null, CancellationToken ct = default);
    Task<BookingWriteResult> TransitionAsync(Guid userId, BookingTransitionForm form, CancellationToken ct = default);
    Task<BookingWriteResult> TaskAsync(Guid userId, CareTaskForm form, CancellationToken ct = default);
    Task<BookingWriteResult> AddTaskAsync(Guid userId, AddCareTaskForm form, CancellationToken ct = default);
    Task<BookingWriteResult> UpdateAsync(Guid userId, BookingUpdateForm form, bool issue, CancellationToken ct = default);
    Task<BookingUpdatePhoto?> PhotoAsync(Guid userId, Guid updateId, CancellationToken ct = default);
}

public sealed class SitterBookingService(PetSittingDbContext db, TimeProvider clock) : ISitterBookingService
{
    private IQueryable<PetBooking> Owned(Guid userId) => db.PetBookings.Where(b => b.SitterProfile.UserId == userId);
    private static IQueryable<PetBooking> Include(IQueryable<PetBooking> query, bool details) => details
        ? query.Include(b => b.Customer).Include(b => b.Request).ThenInclude(r => r.Pets).ThenInclude(p => p.Pet).Include(b => b.Payments).Include(b => b.CareTasks).Include(b => b.Updates).AsSplitQuery()
        : query.Include(b => b.Customer).Include(b => b.Request).ThenInclude(r => r.Pets).ThenInclude(p => p.Pet).Include(b => b.Payments).AsSplitQuery();
    public async Task<SitterBookingsViewModel> LoadAsync(ApplicationUser account, Guid? id = null, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var portal = await PortalData.LoadAsync(db, account, now, ct);
        var rows = await Include(Owned(account.Id).AsNoTracking().Where(b => id == null || b.Id == id), id != null).OrderBy(b => b.StartsAtUtc).ToListAsync(ct);
        return new() { Account = account, Profile = portal.Profile, Now = now, Unread = portal.Unread, IncomingRequestCount = portal.Incoming, Bookings = rows, Booking = id == null ? null : rows.SingleOrDefault() };
    }
    public Task<BookingWriteResult> TransitionAsync(Guid userId, BookingTransitionForm form, CancellationToken ct = default) => WriteAsync(userId, form.Id, (booking, now) =>
    {
        if (form.Action is not ("start" or "complete")) return new(false, "Choose a valid booking action.");
        if (form.Action == "start" && booking.Status == BookingStatus.InProgress || form.Action == "complete" && booking.Status == BookingStatus.Completed) return new(true, "This status has already been saved.");
        if (!VersionMatches(booking.RowVersion, form.Version)) return new(false, "The booking changed. Review the latest details and try again.");
        if (form.Action == "start")
        {
            var block = BookingRules.StartBlock(booking, now);
            if (block is not null) return new(false, block);
            booking.Status = BookingStatus.InProgress; booking.StartedAtUtc = now;
        }
        else
        {
            if (booking.Status != BookingStatus.InProgress) return new(false, "Start care before marking this booking completed.");
            booking.Status = BookingStatus.Completed; booking.CompletedAtUtc = now; booking.Request.Status = RequestStatus.Completed;
        }
        Notify(booking, now, form.Action == "start" ? "Care has started" : "Care is complete", form.Action == "start" ? "Your sitter has checked in and started care." : "Your sitter marked the booking completed.");
        return new(true, form.Action == "start" ? "Care started. The owner was notified." : "Booking completed. The owner was notified.");
    }, ct);
    public Task<BookingWriteResult> TaskAsync(Guid userId, CareTaskForm form, CancellationToken ct = default) => WriteAsync(userId, form.Id, (booking, now) =>
    {
        if (!BookingRules.CanUpdateCare(booking)) return new(false, "Checklist updates are available while care is in progress.");
        var template = BookingRules.Tasks(booking).SingleOrDefault(t => t.Key == form.Key);
        if (template is null) return new(false, "That checklist item is no longer available.", true);
        var task = booking.CareTasks.SingleOrDefault(t => t.Key == form.Key);
        if (task is null) { task = new() { BookingId = booking.Id, Key = template.Key, Label = template.Label, CreatedAtUtc = now }; db.BookingCareTasks.Add(task); }
        task.CompletedAtUtc = form.Completed ? task.CompletedAtUtc ?? now : null;
        return new(true, "Checklist saved.");
    }, ct);
    public Task<BookingWriteResult> AddTaskAsync(Guid userId, AddCareTaskForm form, CancellationToken ct = default) => WriteAsync(userId, form.Id, (booking, now) =>
    {
        if (booking.Status is not (BookingStatus.InProgress or BookingStatus.Confirmed or BookingStatus.AwaitingPayment)) return new(false, "Tasks can only be added to an active booking.");
        if (string.IsNullOrWhiteSpace(form.Label) || form.Label.Trim().Length > 200) return new(false, "Enter a checklist task of up to 200 characters.");
        DateTimeOffset? due = null;
        if (form.DueAtLocal is { } local)
        {
            try { due = ScheduleMath.Utc(DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local)); }
            catch (ArgumentException ex) { return new(false, ex.Message); }
            if (due < booking.StartsAtUtc || due > booking.EndsAtUtc) return new(false, "Choose a task time within the booking dates.");
        }
        db.BookingCareTasks.Add(new() { BookingId = booking.Id, Key = "custom:" + Guid.NewGuid().ToString("N"), Label = form.Label.Trim(), DueAtUtc = due, CreatedAtUtc = now });
        return new(true, "Task added to the care checklist.");
    }, ct);
    public async Task<BookingWriteResult> UpdateAsync(Guid userId, BookingUpdateForm form, bool issue, CancellationToken ct = default)
    {
        if (form.ClientToken == Guid.Empty || form.Message?.Length > 2000 || string.IsNullOrWhiteSpace(form.Message) && form.Photo is null) return new(false, "Write an update or attach a photo.");
        if (issue && string.IsNullOrWhiteSpace(form.Message)) return new(false, "Describe the issue so the owner can respond.");
        byte[]? bytes = null; string? type = null;
        if (form.Photo is not null)
        {
            if (form.Photo.Length is <= 0 or > 5242880) return new(false, "Choose a JPEG, PNG or WebP photo up to 5 MB.");
            await using var stream = form.Photo.OpenReadStream();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + read > 5242880) return new(false, "The photo exceeds the 5 MB limit."); await buffer.WriteAsync(chunk.AsMemory(0, read), ct); }
            bytes = buffer.ToArray(); type = ImageType(bytes);
            if (type is null) return new(false, "Choose a valid JPEG, PNG or WebP photo. Other file types are not supported.");
        }
        return await WriteAsync(userId, form.Id, (booking, now) =>
        {
            if (booking.Updates.Any(u => u.ClientToken == form.ClientToken)) return new(true, "This update has already been sent.");
            if (!issue && booking.Status is not (BookingStatus.InProgress or BookingStatus.Completed)) return new(false, "Start care before sending a care update.");
            var update = new BookingUpdate { BookingId = booking.Id, ClientToken = form.ClientToken, Message = form.Message?.Trim() ?? "", IsIssue = issue, HasPhoto = bytes is not null, CreatedAtUtc = now };
            db.BookingUpdates.Add(update);
            if (bytes is not null) db.BookingUpdatePhotos.Add(new() { BookingUpdateId = update.Id, Bytes = bytes, ContentType = type!, CreatedAtUtc = now });
            Notify(booking, now, issue ? "Your sitter reported an issue" : "New care update from your sitter", string.IsNullOrEmpty(update.Message) ? "Your sitter shared a photo." : update.Message);
            return new(true, issue ? "Issue recorded. The owner was notified." : "Update sent. The owner was notified.");
        }, ct);
    }
    public Task<BookingUpdatePhoto?> PhotoAsync(Guid userId, Guid updateId, CancellationToken ct = default) => db.BookingUpdatePhotos.AsNoTracking().SingleOrDefaultAsync(p => p.BookingUpdateId == updateId && (p.BookingUpdate.Booking.CustomerId == userId || p.BookingUpdate.Booking.SitterProfile.UserId == userId), ct);
    private async Task<BookingWriteResult> WriteAsync(Guid userId, Guid id, Func<PetBooking, DateTimeOffset, BookingWriteResult> operation, CancellationToken ct)
    {
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var booking = await Include(Owned(userId).Where(b => b.Id == id), true).SingleOrDefaultAsync(ct);
                if (booking is null) return new BookingWriteResult(false, "Booking not found.", true);
                var result = operation(booking, clock.GetUtcNow());
                if (!result.Succeeded) return result;
                await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
                return result;
            });
        }
        catch (DbUpdateConcurrencyException) { return new(false, "The booking changed. Refresh and try again."); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(false, "This update has already been processed. Refresh to see the saved result."); }
    }
    private void Notify(PetBooking b, DateTimeOffset now, string title, string body) => db.Notifications.Add(new() { Category = NotificationCategory.Booking, UserId = b.CustomerId, Title = title, Body = body, RelativeUrl = $"/OwnerBookings/Details/{b.RequestId}", CreatedAtUtc = now });
    private static bool VersionMatches(byte[] actual, string version)
    {
        try { return actual.Length == 8 && actual.SequenceEqual(Convert.FromBase64String(version ?? "")); } catch (FormatException) { return false; }
    }
    public static string? ImageType(byte[] data)
    {
        if (data.Length >= 12 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && data.AsSpan(data.Length - 8, 4).SequenceEqual("IEND"u8)) return "image/png";
        if (data.Length >= 4 && data[0] == 255 && data[1] == 216 && data[2] == 255 && data[^2] == 255 && data[^1] == 217) return "image/jpeg";
        if (data.Length >= 16 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8) && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) == data.Length - 8) return "image/webp";
        return null;
    }
}
