using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;

namespace PetSitting.Repositories;

public sealed class PetRepository(PetSittingDbContext db) : IPetRepository
{
    public async Task<IReadOnlyList<Pet>> GetAllForOwnerAsync(Guid ownerId, string? search, string? species)
    {
        // Read-only list: AsNoTracking avoids change-tracking overhead.
        IQueryable<Pet> query = db.Pets.AsNoTracking().Where(p => p.OwnerId == ownerId);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Name.Contains(search) || (p.Breed != null && p.Breed.Contains(search)));

        if (!string.IsNullOrWhiteSpace(species))
            query = query.Where(p => p.Species == species);

        return await query.OrderBy(p => p.Name).ToListAsync();
    }

    public Task<Pet?> GetByIdAsync(Guid id, Guid ownerId) =>
        db.Pets.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == ownerId);

    public async Task AddAsync(Pet pet)
    {
        db.Pets.Add(pet);
        await db.SaveChangesAsync();
    }

    public async Task<bool> UpdateAsync(Pet pet)
    {
        // Load the tracked entity, copy the editable values, then save (entity state: Unchanged → Modified).
        var existing = await db.Pets.FirstOrDefaultAsync(p => p.Id == pet.Id && p.OwnerId == pet.OwnerId);
        if (existing is null) return false;

        existing.Name = pet.Name;
        existing.Species = pet.Species;
        existing.Breed = pet.Breed;
        existing.AgeYears = pet.AgeYears;
        existing.MedicalNotes = pet.MedicalNotes;
        existing.FeedingInstructions = pet.FeedingInstructions;
        existing.CareInstructions = pet.CareInstructions;
        await db.SaveChangesAsync();
        return true;
    }

    public Task<bool> IsUsedInCareRequestsAsync(Guid id) =>
        db.PetCareRequestPets.AnyAsync(x => x.PetId == id);

    public async Task<bool> DeleteAsync(Guid id, Guid ownerId)
    {
        var pet = await db.Pets.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == ownerId);
        if (pet is null) return false;

        db.Pets.Remove(pet);
        await db.SaveChangesAsync();
        return true;
    }
}
