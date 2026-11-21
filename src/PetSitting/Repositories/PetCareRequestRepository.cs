using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.ViewModels;

namespace PetSitting.Repositories;

public enum RequestWriteResult { Saved, Deleted, Cancelled, NotFound, NotEditable, Conflict, InvalidPets }

public sealed class PetCareRequestRepository(PetSittingDbContext db, TimeProvider clock)
{
    public static bool IsEditable(PetCareRequest request, DateTimeOffset now) => request.Status == RequestStatus.Open && request.StartsAtUtc > now;

    public Task<PetCareRequest?> FindAsync(Guid owner, Guid id) => db.PetCareRequests.AsNoTracking()
        .Include(r => r.Pets).Include(r => r.Offers).SingleOrDefaultAsync(r => r.Id == id && r.CustomerId == owner);

    public Task<RequestWriteResult> UpdateAsync(Guid owner, Guid id, RequestEditViewModel form) => WriteAsync(async () =>
    {
        var request = await db.PetCareRequests.Include(r => r.Pets).Include(r => r.Offers).SingleOrDefaultAsync(r => r.Id == id && r.CustomerId == owner);
        if (request is null) return RequestWriteResult.NotFound;
        if (!IsEditable(request, clock.GetUtcNow())) return RequestWriteResult.NotEditable;
        if (!Matches(request.RowVersion, form.Version)) return RequestWriteResult.Conflict;
        var pets = await db.Pets.Where(p => p.OwnerId == owner && form.PetIds.Contains(p.Id)).ToListAsync();
        if (form.PetIds.Count is < 1 or > 20 || form.PetIds.Distinct().Count() != form.PetIds.Count || pets.Count != form.PetIds.Count
            || (form.ServiceType == ServiceType.DogWalking && pets.Any(p => p.Species != "Dog"))) return RequestWriteResult.InvalidPets;
        var times = form.Times()!.Value;
        // Pet link changes alone must also invalidate a sitter's existing confirmation.
        db.Entry(request).Property(r => r.Title).IsModified = true;
        request.Title = form.Title.Trim(); request.Description = form.Description.Trim(); request.ServiceType = form.ServiceType;
        request.StartsAtUtc = times.Start; request.EndsAtUtc = times.End; request.City = form.City.Trim();
        request.ServiceAddress = form.ServiceType == ServiceType.Boarding ? null : form.ServiceAddress?.Trim(); request.Budget = form.Budget;
        db.PetCareRequestPets.RemoveRange(request.Pets.Where(p => !form.PetIds.Contains(p.PetId)));
        foreach (var pet in pets.Where(p => request.Pets.All(link => link.PetId != p.Id)))
            request.Pets.Add(new PetCareRequestPet { PetId = pet.Id });
        // Pending offers take the owner's current price. An offer the sitter already sent was for the old terms, so it goes
        // back to waiting for the sitter, who is asked to review the change and send it again.
        var resend = new List<Guid>();
        foreach (var offer in request.Offers.Where(o => o.Status == OfferStatus.Pending))
        {
            offer.Amount = request.Budget; offer.Currency = request.Currency;
            if (offer.SubmittedAtUtc is not null) { offer.SubmittedAtUtc = null; resend.Add(offer.SitterProfileId); }
        }
        if (resend.Count > 0)
        {
            var now = clock.GetUtcNow();
            foreach (var sitter in await db.PetSitterProfiles.Where(p => resend.Contains(p.Id)).Select(p => p.UserId).ToListAsync())
                db.Notifications.Add(new Notification { UserId = sitter, Category = NotificationCategory.Request,
                    SourceKey = "offer-changed:" + request.Id + ":" + now.UtcTicks, Title = "A request you made an offer on changed",
                    Body = request.Title + " · " + "The owner changed this request (price, dates or details). Review it and send your offer again if it still works for you.", RelativeUrl = "/SitterRequests?id=" + request.Id, CreatedAtUtc = now });
        }
        // Keep the submission snapshot current for status pages and "book again", without
        // breaking the wizard's submitted-request idempotency link.
        var draft = await db.OwnerRequestDrafts.SingleOrDefaultAsync(d => d.SubmittedRequestId == id && d.CustomerId == owner);
        if (draft is not null)
        {
            var snapshot = JsonSerializer.Deserialize<OwnerRequestForm>(draft.DataJson) ?? new();
            snapshot.PetIds = form.PetIds.ToList(); snapshot.ServiceType = request.ServiceType;
            snapshot.City = request.City; snapshot.Instructions = request.Description;
            snapshot.StartDate = DateOnly.FromDateTime(form.StartsAtLocal!.Value); snapshot.StartTime = TimeOnly.FromDateTime(form.StartsAtLocal.Value);
            snapshot.EndDate = DateOnly.FromDateTime(form.EndsAtLocal!.Value); snapshot.EndTime = TimeOnly.FromDateTime(form.EndsAtLocal.Value);
            snapshot.Budget = request.Budget;
            if (snapshot.Address != (request.ServiceAddress ?? ""))
            {
                snapshot.StreetAddress = request.ServiceAddress; snapshot.Area = null; snapshot.Building = null; snapshot.AccessNotes = null;
            }
            draft.DataJson = JsonSerializer.Serialize(snapshot); draft.UpdatedAtUtc = clock.GetUtcNow();
        }
        return RequestWriteResult.Saved;
    });

    public Task<RequestWriteResult> DeleteAsync(Guid owner, Guid id, string version) => WriteAsync(async () =>
    {
        var request = await db.PetCareRequests.Include(r => r.Pets).Include(r => r.Offers).ThenInclude(o => o.SitterProfile)
            .SingleOrDefaultAsync(r => r.Id == id && r.CustomerId == owner);
        if (request is null) return RequestWriteResult.NotFound;
        if (request.Status != RequestStatus.Open) return RequestWriteResult.NotEditable;
        if (!Matches(request.RowVersion, version)) return RequestWriteResult.Conflict;
        if (request.Offers.Count == 0 && !await db.PetBookings.AnyAsync(b => b.RequestId == id)
            && !await db.CombinedCareLinks.AnyAsync(l => l.LocalRequestId == id))
        {
            db.OwnerRequestDrafts.RemoveRange(await db.OwnerRequestDrafts.Where(d => d.SubmittedRequestId == id).ToListAsync());
            db.PetCareRequestPets.RemoveRange(request.Pets);
            db.PetCareRequests.Remove(request);
            return RequestWriteResult.Deleted;
        }
        // Keep linked requests so the status change queues cancellation for the peer in the same save.
        // Responses and past bookings remain as history. Active bookings use booking cancellation.
        request.Status = RequestStatus.Cancelled;
        foreach (var offer in request.Offers.Where(o => o.Status == OfferStatus.Pending))
        {
            offer.Status = OfferStatus.Withdrawn; offer.DecidedAtUtc = clock.GetUtcNow();
            db.Notifications.Add(new() { UserId = offer.SitterProfile.UserId, Category = NotificationCategory.Request,
                Title = "A pet owner cancelled a request", Body = request.Title, RelativeUrl = "/SitterRequests?id=" + id });
        }
        return RequestWriteResult.Cancelled;
    });

    private async Task<RequestWriteResult> WriteAsync(Func<Task<RequestWriteResult>> action)
    {
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                var result = await action();
                if (result is not (RequestWriteResult.Saved or RequestWriteResult.Deleted or RequestWriteResult.Cancelled)) return result;
                await db.SaveChangesAsync(); await tx.CommitAsync(); return result;
            });
        }
        catch (DbUpdateConcurrencyException) { return RequestWriteResult.Conflict; }
    }
    private static bool Matches(byte[] actual, string? version)
    {
        try { return actual.Length == 8 && actual.SequenceEqual(Convert.FromBase64String(version ?? "")); }
        catch (FormatException) { return false; }
    }
}
