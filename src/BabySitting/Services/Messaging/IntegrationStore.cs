using System.Data;
using CareConnect.Contracts.Messaging;
using CareConnect.Messaging;
using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;
namespace BabySitting.Services.Messaging;

public sealed class IntegrationStore(BabySittingDbContext db) : IIntegrationStore
{
    public async Task<IReadOnlyList<OutgoingMessage>> PendingAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.MessageOutboxEntries.AsNoTracking().Where(m => m.PublishedAtUtc == null &&
            (m.NextAttemptAtUtc == null || m.NextAttemptAtUtc <= now)).OrderBy(m => m.CreatedAtUtc).Take(20)
            .Select(m => new OutgoingMessage(m.Id, m.CorrelationId, m.RoutingKey, m.PayloadJson)).ToListAsync(ct);
    }
    public Task PublishedAsync(Guid id, CancellationToken ct) => db.MessageOutboxEntries.Where(m => m.Id == id && m.PublishedAtUtc == null)
        .ExecuteUpdateAsync(s => s.SetProperty(m => m.PublishedAtUtc, DateTimeOffset.UtcNow).SetProperty(m => m.LastError, (string?)null), ct);
    public async Task FailedAsync(Guid id, CancellationToken ct)
    {
        var attempts = await db.MessageOutboxEntries.Where(m => m.Id == id).Select(m => m.Attempts).SingleAsync(ct);
        var next = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts + 1, 9))));
        await db.MessageOutboxEntries.Where(m => m.Id == id && m.PublishedAtUtc == null).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Attempts, m => m.Attempts + 1).SetProperty(m => m.NextAttemptAtUtc, next)
            .SetProperty(m => m.LastError, "Broker delivery failed; automatic retry scheduled."), ct);
    }

    public async Task ReceiveAsync(CombinedCareEvent message, CancellationToken ct)
    {
        Validate(message);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var meta = message.Metadata;
            if (await db.ProcessedMessages.AnyAsync(m => m.EventId == meta.EventId && m.Consumer == "combined-v1", ct)) return;
            var link = await db.CombinedCareLinks.SingleOrDefaultAsync(l => l.Id == meta.CorrelationId, ct);
            if (link is null)
            {
                if (meta.EventType != "Invited") throw new InvalidOperationException("Invitation has not arrived yet.");
                link = new CombinedCareLink { Id = meta.CorrelationId, OriginPlatform = "PetSitting", PeerRequestId = message.RequestId,
                    InvitationHash = message.InvitationHash!, InvitationExpiresAtUtc = message.InvitationExpiresAtUtc!.Value };
                db.CombinedCareLinks.Add(link);
            }
            if (meta.EventType == "Invited" && link.OriginPlatform == "BabySitting")
                throw new InvalidDataException("Invitation has an invalid origin.");
            if (meta.EventType == "State" && link.OriginPlatform == "BabySitting" && link.PeerRequestId is null)
                throw new InvalidOperationException("Invitation acceptance has not arrived yet.");
            if (meta.EventType == "Joined" && message.InvitationHash != link.InvitationHash)
                throw new InvalidDataException("Invalid invitation proof.");
            if (link.PeerRequestId is { } peer && peer != message.RequestId)
                throw new InvalidDataException("A different request is already linked.");
            if (meta.EventType == "Joined" && (link.OriginPlatform != "BabySitting" || link.LocalRequestId != message.PeerRequestId))
                throw new InvalidDataException("Invalid invitation acceptance.");
            if (message.PeerRequestId is { } target && link.LocalRequestId is { } local && target != local)
                throw new InvalidDataException("Unexpected target request.");
            if (message.Revision > link.PeerRevision)
            {
                if (link.OriginPlatform != "BabySitting" && link.LocalRequestId is null)
                {
                    link.InvitationHash = message.InvitationHash!;
                    link.InvitationExpiresAtUtc = message.InvitationExpiresAtUtc!.Value;
                }
                link.PeerRequestId = message.RequestId;
                link.PeerRevision = message.Revision;
                link.PeerRequestStatus = message.RequestStatus;
                link.PeerBookingStatus = message.BookingStatus;
                link.LastPeerUpdateAtUtc = DateTimeOffset.UtcNow;
                if (link.LocalRequestId is { } requestId)
                {
                    var request = await db.ChildcareRequests.SingleAsync(r => r.Id == requestId, ct);
                    request.ExternalPlatform = "PetSitting";
                    request.ExternalRequestId = message.RequestId;
                    if (meta.EventType == "Joined")
                    {
                        // A cancellation or payment may have happened while acceptance was in transit.
                        var currentBooking = await db.BabysittingBookings.Where(b => b.RequestId == requestId)
                            .OrderByDescending(b => b.Status != BookingStatus.Cancelled && b.Status != BookingStatus.Rejected)
                            .ThenByDescending(b => b.CreatedAtUtc).FirstOrDefaultAsync(ct);
                        CombinedCareOutbox.Enqueue(db, link, request, currentBooking?.Status.ToString(), "State");
                    }
                    if (message.RequestStatus == "Cancelled") await CancelLocalAsync(link, request, ct);
                    if (request.CustomerId is { } customer)
                        db.Notifications.Add(new Notification { UserId = customer, Category = NotificationCategory.Booking,
                            SourceKey = "combined:" + meta.EventId, Title = "Combined care updated",
                            Body = "PetSitting: " + message.RequestStatus + (message.BookingStatus is null ? "" : " / " + message.BookingStatus),
                            RelativeUrl = "/CombinedCare" });
                }
            }
            db.ProcessedMessages.Add(new ProcessedMessage { EventId = meta.EventId, CorrelationId = meta.CorrelationId, Consumer = "combined-v1" });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }

    private async Task CancelLocalAsync(CombinedCareLink link, ChildcareRequest request, CancellationToken ct)
    {
        if (request.Status == RequestStatus.Cancelled) return;
        var bookings = await db.BabysittingBookings.Where(b => b.RequestId == request.Id).Include(b => b.Payments)
            .Include(b => b.Offer).Include(b => b.SitterProfile).ToListAsync(ct);
        if (request.Status == RequestStatus.Completed || bookings.Any(b => b.Status is BookingStatus.InProgress or BookingStatus.Completed))
        {
            link.CancellationIssue = "The other service was cancelled. Care here has already started or completed; contact your sitter to resolve this service separately.";
            return;
        }
        link.CancellationIssue = null;
        request.Status = RequestStatus.Cancelled;
        foreach (var booking in bookings.Where(b => b.Status is BookingStatus.Pending or BookingStatus.AwaitingPayment or BookingStatus.Confirmed))
        {
            booking.Status = BookingStatus.Cancelled;
            booking.CancelledAtUtc = DateTimeOffset.UtcNow;
            booking.CancellationReason = "The customer cancelled the linked combined-care request.";
            booking.Offer.Status = OfferStatus.Withdrawn;
            foreach (var payment in booking.Payments)
            {
                if (payment.Status == PaymentStatus.Succeeded) payment.Status = PaymentStatus.Refunded;
                else if (payment.Status == PaymentStatus.Pending) { payment.Status = PaymentStatus.Failed; payment.FailureReason = "Combined care cancelled."; }
            }
            db.Notifications.Add(new Notification { UserId = booking.SitterProfile.UserId, Category = NotificationCategory.Booking,
                SourceKey = "booking-cancelled:" + booking.Id, Title = "Combined booking cancelled",
                Body = request.Title, RelativeUrl = "/SitterBookings/Details/" + booking.Id });
        }
        // Pending offers must no longer be actionable after the linked cancellation.
        var offers = await db.Set<BabysittingOffer>().Where(o => o.RequestId == request.Id && o.Status == OfferStatus.Pending).ToListAsync(ct);
        foreach (var offer in offers) { offer.Status = OfferStatus.Withdrawn; offer.DecidedAtUtc = DateTimeOffset.UtcNow; }
    }

    public static void Validate(CombinedCareEvent message)
    {
        var m = message.Metadata;
        if (m is null || m.Source != "PetSitting" || m.Version != 1 || m.EventId == Guid.Empty || m.CorrelationId == Guid.Empty ||
            message.RequestId == Guid.Empty || message.Revision < 1 ||
            m.EventType is not ("Invited" or "Joined" or "State") ||
            !Enum.TryParse<RequestStatus>(message.RequestStatus, out var status) || !Enum.IsDefined(status) || status.ToString() != message.RequestStatus ||
            message.BookingStatus is not null && (!Enum.TryParse<BookingStatus>(message.BookingStatus, out var booking) || !Enum.IsDefined(booking) || booking.ToString() != message.BookingStatus))
            throw new InvalidDataException("Unsupported combined-care event.");
        if (m.EventType == "Invited" && (message.InvitationHash is not { Length: 64 } || message.InvitationExpiresAtUtc is null))
            throw new InvalidDataException("Invalid invitation.");
        if (m.EventType == "Joined" && message.PeerRequestId is null)
            throw new InvalidDataException("Missing originating request.");
    }
}
