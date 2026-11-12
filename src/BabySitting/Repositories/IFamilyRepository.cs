using BabySitting.Models;

namespace BabySitting.Repositories;

// A parent account has at most one family profile, so every method works with the signed-in user's id.
public interface IFamilyRepository
{
    Task<FamilyProfile?> GetForUserAsync(Guid userId);
    Task AddAsync(FamilyProfile family);
    Task<bool> UpdateAsync(FamilyProfile family);
    Task<bool> IsUsedInRequestsAsync(Guid familyId);
    Task<bool> DeleteAsync(Guid userId);
}
