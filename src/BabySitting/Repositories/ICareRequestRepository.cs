using BabySitting.Models;

namespace BabySitting.Repositories;

public enum CareRequestWriteResult { Saved, Deleted, Cancelled, NotFound, NotEditable, Conflict }

// Every method takes the signed-in parent's id, so a parent can never read or change another family's request.
public interface ICareRequestRepository
{
    Task<IReadOnlyList<ChildcareRequest>> GetAllForParentAsync(Guid parentId);
    Task<ChildcareRequest?> GetByIdAsync(Guid id, Guid parentId);
    Task<IReadOnlyList<BabysittingBooking>> GetBookingsForParentAsync(Guid parentId, Guid? requestId = null);
    Task AddAsync(ChildcareRequest request);
    Task<CareRequestWriteResult> UpdateAsync(Guid parentId, Guid id, byte[] version, Action<ChildcareRequest> apply, DateTimeOffset now);
    Task<CareRequestWriteResult> DeleteOrCancelAsync(Guid parentId, Guid id);
    // Only an open request whose start is still in the future can be edited.
    static bool IsEditable(ChildcareRequest request, DateTimeOffset now) => request.Status == RequestStatus.Open && request.StartsAtUtc > now;
}
