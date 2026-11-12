using PetSitting.Models;

namespace PetSitting.Repositories;

// Every method is scoped to one owner, so an owner can never read or change another owner's pets.
public interface IPetRepository
{
    Task<IReadOnlyList<Pet>> GetAllForOwnerAsync(Guid ownerId, string? search, string? species);
    Task<Pet?> GetByIdAsync(Guid id, Guid ownerId);
    Task AddAsync(Pet pet);
    Task<bool> UpdateAsync(Pet pet);
    Task<bool> IsUsedInCareRequestsAsync(Guid id);
    Task<bool> DeleteAsync(Guid id, Guid ownerId);
}
