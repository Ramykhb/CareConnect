using System.Text.Json;
using CareConnect.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;
namespace BabySitting.Services.Messaging;

public static class CombinedCareOutbox
{
    public static void Enqueue(BabySittingDbContext db, CombinedCareLink link, ChildcareRequest request, string? bookingStatus, string type)
    {
        link.LocalRevision++;
        var metadata = new EventMetadata(Guid.NewGuid(), link.Id, type, 1, "BabySitting", DateTimeOffset.UtcNow);
        var message = new CombinedCareEvent(metadata, request.Id, link.PeerRequestId, link.LocalRevision,
            request.Status.ToString(), bookingStatus,
            link.InvitationHash, link.InvitationExpiresAtUtc);
        db.MessageOutboxEntries.Add(new MessageOutbox { Id = metadata.EventId, CorrelationId = link.Id,
            EventType = type, RoutingKey = "BabySitting".ToLowerInvariant() + ".combined." + type.ToLowerInvariant(),
            PayloadJson = JsonSerializer.Serialize(message) });
    }

    // Called before the same SaveChanges that persists a lifecycle transition, including
    // saves inside explicit business transactions. No broker call occurs on the write path.
    public static async Task CaptureAsync(BabySittingDbContext db, CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        var requestIds = db.ChangeTracker.Entries<ChildcareRequest>()
            .Where(e => e.State == EntityState.Modified && e.Property(r => r.Status).IsModified)
            .Select(e => e.Entity.Id)
            .Concat(db.ChangeTracker.Entries<BabysittingBooking>().Where(e => e.State == EntityState.Added ||
                e.State == EntityState.Modified && e.Property(b => b.Status).IsModified).Select(e => e.Entity.RequestId))
            .Distinct().ToArray();
        foreach (var id in requestIds)
        {
            var link = db.CombinedCareLinks.Local.FirstOrDefault(l => l.LocalRequestId == id)
                ?? await db.CombinedCareLinks.SingleOrDefaultAsync(l => l.LocalRequestId == id, ct);
            if (link is null) continue;
            var request = db.ChildcareRequests.Local.FirstOrDefault(r => r.Id == id)
                ?? await db.ChildcareRequests.SingleAsync(r => r.Id == id, ct);
            var localBookings = db.BabysittingBookings.Local.Where(b => b.RequestId == id).ToArray();
            var persisted = await db.BabysittingBookings.Where(b => b.RequestId == id).ToListAsync(ct);
            var current = persisted.Concat(localBookings).DistinctBy(b => b.Id)
                .OrderByDescending(b => b.Status is not (BookingStatus.Cancelled or BookingStatus.Rejected))
                .ThenByDescending(b => b.CreatedAtUtc).FirstOrDefault();
            Enqueue(db, link, request, current?.Status.ToString(), "State");
        }
    }
}
