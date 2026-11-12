using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;

namespace BabySitting.Repositories;

public sealed class FamilyRepository(BabySittingDbContext db) : IFamilyRepository
{
    // Read-only query: AsNoTracking avoids change-tracking overhead.
    public Task<FamilyProfile?> GetForUserAsync(Guid userId) =>
        db.FamilyProfiles.AsNoTracking().FirstOrDefaultAsync(f => f.UserId == userId);

    public async Task AddAsync(FamilyProfile family)
    {
        db.FamilyProfiles.Add(family);
        await db.SaveChangesAsync();
    }

    public async Task<bool> UpdateAsync(FamilyProfile family)
    {
        // Load the tracked entity, copy the editable values, then save (entity state: Unchanged → Modified).
        var existing = await db.FamilyProfiles.FirstOrDefaultAsync(f => f.UserId == family.UserId);
        if (existing is null) return false;

        existing.NumberOfChildren = family.NumberOfChildren;
        existing.YoungestChildAgeYears = family.YoungestChildAgeYears;
        existing.OldestChildAgeYears = family.OldestChildAgeYears;
        existing.City = family.City;
        existing.HealthNotes = family.HealthNotes;
        existing.Routines = family.Routines;
        existing.CarePreferences = family.CarePreferences;
        await db.SaveChangesAsync();
        return true;
    }

    public Task<bool> IsUsedInRequestsAsync(Guid familyId) =>
        db.ChildcareRequests.AnyAsync(r => r.FamilyProfileId == familyId);

    public async Task<bool> DeleteAsync(Guid userId)
    {
        var family = await db.FamilyProfiles.FirstOrDefaultAsync(f => f.UserId == userId);
        if (family is null) return false;

        db.FamilyProfiles.Remove(family);
        await db.SaveChangesAsync();
        return true;
    }
}
