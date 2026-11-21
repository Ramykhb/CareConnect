using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Bookings;
using PetSitting.Services.Scheduling;
using PetSitting.ViewModels;

namespace PetSitting.Services.Portal;

public sealed record VerificationFile(byte[] Bytes, string ContentType, string FileName);
public sealed record VerificationItem(Guid Id, string SitterName, string? SitterEmail, string City, int YearsOfExperience,
    VerificationDocument DocumentType, string FileName, string ContentType, string? Note, VerificationStatus Status,
    DateTimeOffset SubmittedAtUtc, DateTimeOffset? ReviewedAtUtc, string? DecisionReason, byte[] RowVersion)
{
    public string Version => Convert.ToBase64String(RowVersion);
}

// Sitters send proof of who they are; platform managers approve or reject it. Approved sitters show a Verified badge.
public interface ISitterVerificationService
{
    Task<SitterVerificationViewModel> PageAsync(ApplicationUser account, CancellationToken ct = default);
    Task<PortalWriteResult> SubmitAsync(Guid userId, VerificationForm form, Stream? file, string? fileName, long length, CancellationToken ct = default);
    Task<VerificationFile?> FileAsync(Guid id, Guid userId, bool manager, CancellationToken ct = default);
    Task<IReadOnlyList<VerificationItem>> ListAsync(VerificationStatus? status, CancellationToken ct = default);
    Task<PortalWriteResult> DecideAsync(Guid managerId, Guid id, bool approve, string? reason, string? version, CancellationToken ct = default);
}

public sealed class SitterVerificationService(PetSittingDbContext db, UserManager<ApplicationUser> users, TimeProvider clock) : ISitterVerificationService
{
    public const long MaxBytes = 5 * 1024 * 1024;

    public static string DocumentLabel(VerificationDocument type) => type switch
    {
        VerificationDocument.NationalId => "National ID card", VerificationDocument.Passport => "Passport",
        VerificationDocument.DrivingLicence => "Driving licence", _ => "Pet care certificate"
    };

    public async Task<SitterVerificationViewModel> PageAsync(ApplicationUser account, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var portal = await PortalData.LoadAsync(db, account, now, ct);
        var history = portal.Profile is null ? [] : await Items(db.SitterVerifications.Where(v => v.SitterProfileId == portal.Profile.Id)
            .OrderByDescending(v => v.CreatedAtUtc).Take(20)).ToListAsync(ct);
        return new() { Account = account, Profile = portal.Profile, Now = now, Unread = portal.Unread, IncomingRequestCount = portal.Incoming, History = history };
    }

    public async Task<PortalWriteResult> SubmitAsync(Guid userId, VerificationForm form, Stream? file, string? fileName, long length, CancellationToken ct = default)
    {
        if (file is null || length <= 0) return new(false, "Choose a photo or PDF of your document.");
        if (length > MaxBytes) return new(false, "That file is too big. Choose one up to 5 MB.");
        if (!Enum.IsDefined(form.DocumentType)) return new(false, "Choose the kind of document you are sending.");
        var note = Clean(form.Note);
        if (note?.Length > 500) return new(false, "Keep the note under 500 characters.");
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await file.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) return new(false, "That file is too big. Choose one up to 5 MB.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        var type = FileType(bytes);
        if (type is null) return new(false, "Choose a JPEG, PNG or WebP photo, or a PDF. Other file types are not supported.");

        var profile = await db.PetSitterProfiles.Include(p => p.User).SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null) return new(false, "Set up your sitter profile in Settings before asking to be verified.");
        if (profile.VerificationStatus == VerificationStatus.Verified) return new(false, "You are already verified.");
        if (await db.SitterVerifications.AnyAsync(v => v.SitterProfileId == profile.Id && v.Status == VerificationStatus.Pending, ct))
            return new(false, "Your proof is already waiting for a manager. You'll get a notification when it has been checked.");

        var now = clock.GetUtcNow();
        var verification = new SitterVerification { SitterProfileId = profile.Id, DocumentType = form.DocumentType, FileName = CleanName(fileName, type),
            ContentType = type, Bytes = bytes, Note = note, CreatedAtUtc = now };
        db.SitterVerifications.Add(verification);
        profile.VerificationStatus = VerificationStatus.Pending;
        profile.VerifiedAtUtc = null;
        var name = profile.User.FirstName + " " + profile.User.LastName;
        async Task AlertManagersAsync()
        {
            foreach (var manager in (await users.GetUsersInRoleAsync(Roles.PlatformManager)).Where(m => m.IsActive))
                db.Notifications.Add(new Notification { UserId = manager.Id, Category = NotificationCategory.General, SourceKey = "verification:" + verification.Id,
                    Title = "Sitter verification to review", Body = name + " sent a " + DocumentLabel(form.DocumentType).ToLowerInvariant() + " to be verified.",
                    RelativeUrl = "/SitterVerifications", CreatedAtUtc = now });
        }
        await AlertManagersAsync();
        for (var attempt = 1; ; attempt++)
        {
            try { await db.SaveChangesAsync(ct); break; }
            catch (DbUpdateException ex) when (attempt < 3 && ex.InnerException is SqlException { Number: 547 })
            {
                // A manager account was removed while the alerts were being saved: alert the current managers instead.
                foreach (var entry in db.ChangeTracker.Entries<Notification>().Where(e => e.State == EntityState.Added).ToList()) entry.State = EntityState.Detached;
                await AlertManagersAsync();
            }
            catch (DbUpdateException) { return new(false, "Your proof is already waiting for a manager. Refresh the page."); }
        }
        return new(true, "Thanks! Your proof was sent. A manager will check it and you'll get a notification.");
    }

    public async Task<VerificationFile?> FileAsync(Guid id, Guid userId, bool manager, CancellationToken ct = default) =>
        await db.SitterVerifications.AsNoTracking().Where(v => v.Id == id && (manager || v.SitterProfile.UserId == userId))
            .Select(v => new VerificationFile(v.Bytes, v.ContentType, v.FileName)).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<VerificationItem>> ListAsync(VerificationStatus? status, CancellationToken ct = default) =>
        await Items(db.SitterVerifications.Where(v => status == null || v.Status == status)
            .OrderBy(v => v.Status == VerificationStatus.Pending ? 0 : 1).ThenByDescending(v => v.CreatedAtUtc).Take(200)).ToListAsync(ct);

    public async Task<PortalWriteResult> DecideAsync(Guid managerId, Guid id, bool approve, string? reason, string? version, CancellationToken ct = default)
    {
        var verification = await db.SitterVerifications.Include(v => v.SitterProfile).SingleOrDefaultAsync(v => v.Id == id, ct);
        if (verification is null) return new(false, "Verification request not found.", true);
        if (verification.Status != VerificationStatus.Pending) return new(false, "This request was already decided.");
        if (!Matches(verification.RowVersion, version)) return new(false, "This request changed. Refresh the list and try again.");
        reason = Clean(reason);
        if (!approve && reason is null) return new(false, "Write a short reason so the sitter knows what to fix.");
        if (reason?.Length > 500) return new(false, "Keep the reason under 500 characters.");
        var now = clock.GetUtcNow();
        verification.Status = approve ? VerificationStatus.Verified : VerificationStatus.Rejected;
        verification.ReviewedById = managerId;
        verification.ReviewedAtUtc = now;
        verification.DecisionReason = reason;
        verification.SitterProfile.VerificationStatus = verification.Status;
        verification.SitterProfile.VerifiedAtUtc = approve ? now : null;
        db.Notifications.Add(new Notification { UserId = verification.SitterProfile.UserId, Category = NotificationCategory.General,
            SourceKey = "verification-decided:" + verification.Id, Title = approve ? "You're verified" : "Verification not approved",
            Body = approve ? "Customers now see a Verified badge on your profile." : "Reason: " + reason + " You can send new proof.",
            RelativeUrl = "/Sitter/Verification", CreatedAtUtc = now });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return new(false, "This request changed. Refresh the list and try again."); }
        return new(true, approve ? "Sitter verified. They were notified." : "Request rejected. The sitter was notified.");
    }

    // PDFs start with "%PDF-"; photos are checked the same way as booking photos.
    public static string? FileType(byte[] data) =>
        data.Length >= 5 && data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46 && data[4] == 0x2D
            ? "application/pdf" : SitterBookingService.ImageType(data);

    public static string CleanName(string? name, string type)
    {
        var clean = new string(Path.GetFileName((name ?? "").Replace('\\', '/')).Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/')).ToArray()).Trim();
        if (clean.Length > 150) clean = clean[..150];
        return clean.Length > 0 ? clean : "document" + (type == "application/pdf" ? ".pdf" : "");
    }

    private static IQueryable<VerificationItem> Items(IQueryable<SitterVerification> query) =>
        query.Select(v => new VerificationItem(v.Id, v.SitterProfile.User.FirstName + " " + v.SitterProfile.User.LastName, v.SitterProfile.User.Email,
            v.SitterProfile.City, v.SitterProfile.YearsOfExperience, v.DocumentType, v.FileName, v.ContentType, v.Note, v.Status,
            v.CreatedAtUtc, v.ReviewedAtUtc, v.DecisionReason, v.RowVersion));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool Matches(byte[] actual, string? value) { try { return actual.Length == 8 && actual.SequenceEqual(Convert.FromBase64String(value ?? "")); } catch (FormatException) { return false; } }
}
