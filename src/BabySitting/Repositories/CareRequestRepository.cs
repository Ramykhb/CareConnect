using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;

namespace BabySitting.Repositories;

public sealed class CareRequestRepository(BabySittingDbContext db) : ICareRequestRepository
{
    // Read-only queries use AsNoTracking because the results are only displayed.
    public async Task<IReadOnlyList<ChildcareRequest>> GetAllForParentAsync(Guid parentId) =>
        await db.ChildcareRequests.AsNoTracking()
            .Where(r => r.CustomerId == parentId)
            .Include(r => r.Offers).ThenInclude(o => o.SitterProfile).ThenInclude(p => p.User)
            .AsSplitQuery()
            .OrderByDescending(r => r.StartsAtUtc > DateTimeOffset.UtcNow).ThenBy(r => r.StartsAtUtc)
            .ToListAsync();

    public Task<ChildcareRequest?> GetByIdAsync(Guid id, Guid parentId) =>
        db.ChildcareRequests.AsNoTracking()
            .Include(r => r.FamilyProfile)
            .Include(r => r.Offers).ThenInclude(o => o.SitterProfile).ThenInclude(p => p.User)
            .AsSplitQuery()
            .SingleOrDefaultAsync(r => r.Id == id && r.CustomerId == parentId);

    public async Task<IReadOnlyList<BabysittingBooking>> GetBookingsForParentAsync(Guid parentId, Guid? requestId = null) =>
        await db.BabysittingBookings.AsNoTracking()
            .Where(b => b.CustomerId == parentId && (requestId == null || b.RequestId == requestId))
            .Include(b => b.SitterProfile).ThenInclude(p => p.User)
            .Include(b => b.Payments)
            .Include(b => b.Review)
            .AsSplitQuery()
            .OrderByDescending(b => b.CreatedAtUtc)
            .ToListAsync();

    public async Task AddAsync(ChildcareRequest request)
    {
        db.ChildcareRequests.Add(request);
        await db.SaveChangesAsync();
    }

    public async Task<CareRequestWriteResult> UpdateAsync(Guid parentId, Guid id, byte[] version, Action<ChildcareRequest> apply, DateTimeOffset now)
    {
        var request = await db.ChildcareRequests.SingleOrDefaultAsync(r => r.Id == id && r.CustomerId == parentId);
        if (request is null) return CareRequestWriteResult.NotFound;
        if (!ICareRequestRepository.IsEditable(request, now)) return CareRequestWriteResult.NotEditable;
        // The version the parent loaded becomes the original value, so EF Core rejects the save
        // if a sitter accepted (or the parent edited in another tab) in the meantime.
        db.Entry(request).Property(r => r.RowVersion).OriginalValue = version;
        apply(request);
        try
        {
            await db.SaveChangesAsync();
            return CareRequestWriteResult.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            return CareRequestWriteResult.Conflict;
        }
    }

    public async Task<CareRequestWriteResult> DeleteOrCancelAsync(Guid parentId, Guid id, string? version = null)
    {
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                var request = await db.ChildcareRequests.Include(r => r.Offers).ThenInclude(o => o.SitterProfile)
                    .SingleOrDefaultAsync(r => r.Id == id && r.CustomerId == parentId);
                if (request is null) return CareRequestWriteResult.NotFound;
                if (request.Status != RequestStatus.Open) return CareRequestWriteResult.NotEditable;
                if (version is not null)
                {
                    byte[] original;
                    try { original = Convert.FromBase64String(version); } catch (FormatException) { return CareRequestWriteResult.Conflict; }
                    if (original.Length != 8 || !request.RowVersion.SequenceEqual(original)) return CareRequestWriteResult.Conflict;
                }
                var delete = request.Offers.Count == 0 && !await db.BabysittingBookings.AnyAsync(b => b.RequestId == id)
                    && !await db.CombinedCareLinks.AnyAsync(l => l.LocalRequestId == id);
                if (delete)
                {
                    // Wizard submissions retain a draft FK. Delete both in the same transaction.
                    db.ParentRequestDrafts.RemoveRange(await db.ParentRequestDrafts.Where(d => d.SubmittedRequestId == id).ToListAsync());
                    db.ChildcareRequests.Remove(request);
                }
                else
                {
                    // Keep linked requests so the status change queues cancellation for the peer in the same save.
                    request.Status = RequestStatus.Cancelled;
                    foreach (var offer in request.Offers.Where(o => o.Status == OfferStatus.Pending))
                    {
                        offer.Status = OfferStatus.Withdrawn; offer.DecidedAtUtc = DateTimeOffset.UtcNow;
                        db.Notifications.Add(new() { UserId = offer.SitterProfile.UserId, Category = NotificationCategory.Request,
                            Title = "A parent cancelled a request", Body = request.Title, RelativeUrl = "/SitterRequests?id=" + id });
                    }
                }
                await db.SaveChangesAsync(); await tx.CommitAsync();
                return delete ? CareRequestWriteResult.Deleted : CareRequestWriteResult.Cancelled;
            });
        }
        catch (DbUpdateConcurrencyException) { return CareRequestWriteResult.Conflict; }
    }
}
