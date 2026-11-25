using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Bookings;

namespace PetSitting.Services.Payments;

public sealed record StripeWebhookResult(int StatusCode, string Message);

// Applies the events that must not depend on the browser being open:
// payment_intent.succeeded records the paid booking so the sitter's in-app earnings and notifications update.
// The raw body signature is verified before any event is processed.
public sealed class StripeWebhookHandler(PetSittingDbContext db, IOptions<StripeOptions> options, TimeProvider clock, ILogger<StripeWebhookHandler> logger)
{
    private readonly StripeOptions _options = options.Value;

    public async Task<StripeWebhookResult> HandleAsync(string json, string? signature, CancellationToken ct = default)
    {
        if (!_options.IsConfigured || string.IsNullOrWhiteSpace(_options.WebhookSecret))
            return new(503, "Stripe webhooks are not configured.");
        if (string.IsNullOrWhiteSpace(signature)) return new(400, "Missing Stripe-Signature header.");

        Event stripeEvent;
        try
        {
            // Event API versions can differ from the SDK's version; signature verification is what matters here.
            stripeEvent = EventUtility.ConstructEvent(json, signature, _options.WebhookSecret, throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex, "Stripe webhook signature verification failed.");
            return new(400, "Invalid signature.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stripe webhook payload could not be parsed.");
            return new(400, "Invalid payload.");
        }

        try
        {
            switch (stripeEvent.Type)
            {
                case EventTypes.PaymentIntentSucceeded when stripeEvent.Data.Object is PaymentIntent intent:
                    await RecordPaymentAsync(intent, ct);
                    break;
            }
            return new(200, "ok");
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogWarning(ex, "Stripe webhook {EventType} hit a concurrent change; Stripe will retry.", stripeEvent.Type);
            return new(500, "Concurrent update.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // The browser or another delivery recorded this payment first; the unique indexes enforced it.
            return new(200, "Already recorded.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe webhook {EventType} handling failed; Stripe will retry.", stripeEvent.Type);
            return new(500, "Webhook handling failed.");
        }
    }

    private async Task RecordPaymentAsync(PaymentIntent intent, CancellationToken ct)
    {
        if (!string.Equals(intent.Status, "succeeded", StringComparison.OrdinalIgnoreCase)) return;
        if (!Guid.TryParse(intent.Metadata.GetValueOrDefault("bookingId"), out var bookingId) ||
            !Guid.TryParse(intent.Metadata.GetValueOrDefault("payerId"), out var payerId))
        {
            logger.LogWarning("Stripe payment {PaymentIntentId} has no booking metadata; ignored.", intent.Id);
            return;
        }
        var amount = StripePaymentService.FromMinorUnits(intent.Amount, intent.Currency);
        if (amount <= 0) return;

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var booking = await db.PetBookings.Include(b => b.Payments).Include(b => b.Request).Include(b => b.SitterProfile)
                .SingleOrDefaultAsync(b => b.Id == bookingId && b.CustomerId == payerId, ct);
            if (booking is null)
            {
                logger.LogWarning("Stripe payment {PaymentIntentId} references an unknown booking {BookingId}.", intent.Id, bookingId);
                return;
            }
            if (booking.Payments.Any(p => p.Provider == "Stripe" && p.ProviderTransactionId == intent.Id)) return;
            if (!string.Equals(intent.Currency, booking.Currency, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Stripe payment {PaymentIntentId} currency {Currency} does not match booking {BookingId}.", intent.Id, intent.Currency, bookingId);
                return;
            }

            var now = clock.GetUtcNow();
            var payment = new Payment
            {
                BookingId = booking.Id,
                PayerId = payerId,
                Amount = amount,
                Currency = booking.Currency,
                Status = PaymentStatus.Succeeded,
                Provider = "Stripe",
                ProviderTransactionId = intent.Id,
                IdempotencyKey = IdempotencyKey(intent.Id),
                PaidAtUtc = now,
                CreatedAtUtc = now
            };
            db.Payments.Add(payment);

            if (booking.Status == BookingStatus.AwaitingPayment &&
                (booking.TotalAmount == 0 || BookingRules.Paid(booking) + amount >= booking.TotalAmount))
            {
                booking.Status = BookingStatus.Confirmed;
                db.Notifications.Add(new Notification
                {
                    Category = NotificationCategory.Payment,
                    SourceKey = $"payment:{payment.Id}:{PaymentStatus.Succeeded}",
                    UserId = booking.SitterProfile.UserId,
                    Title = "Payment received",
                    Body = booking.Request.Title + " is paid and confirmed.",
                    RelativeUrl = "/SitterBookings/Details/" + booking.Id,
                    CreatedAtUtc = now
                });
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    // Stable per PaymentIntent, so redelivering the same event cannot create a second payment row.
    private static Guid IdempotencyKey(string paymentIntentId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("stripe-payment:" + paymentIntentId))[..16]);
}
