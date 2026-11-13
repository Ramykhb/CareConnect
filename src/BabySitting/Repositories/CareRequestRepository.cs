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

    public async Task<CareRequestWriteResult> DeleteOrCancelAsync(Guid parentId, Guid id)
    {
        var request = await db.ChildcareRequests.Include(r => r.Offers).SingleOrDefaultAsync(r => r.Id == id && r.CustomerId == parentId);
        if (request is null) return CareRequestWriteResult.NotFound;
        // A booked request is cancelled through its booking; finished requests stay as history.
        if (request.Status != RequestStatus.Open) return CareRequestWriteResult.NotEditable;
        try
        {
            if (request.Offers.Count == 0)
            {
                db.ChildcareRequests.Remove(request);
                await db.SaveChangesAsync();
                return CareRequestWriteResult.Deleted;
            }
            // Sitters already responded, so keep the record and mark it cancelled instead.
            request.Status = RequestStatus.Cancelled;
            await db.SaveChangesAsync();
            return CareRequestWriteResult.Cancelled;
        }
        catch (DbUpdateConcurrencyException)
        {
            return CareRequestWriteResult.Conflict;
        }
    }
}
