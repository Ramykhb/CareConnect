using Microsoft.Extensions.Options;
using Stripe;

namespace BabySitting.Services.Payments;

public sealed record StripeIntentResult(bool Succeeded, string? ClientSecret, string? Error);
public sealed record StripeVerificationResult(bool Succeeded, string? Error);

public interface IStripePaymentService
{
    bool IsConfigured { get; }
    string PublishableKey { get; }
    Task<StripeIntentResult> CreateIntentAsync(decimal amount, string currency, Guid bookingId, Guid payerId, string description, CancellationToken ct = default);
    Task<StripeVerificationResult> VerifyAsync(string paymentIntentId, Guid bookingId, Guid payerId, decimal amount, string currency, CancellationToken ct = default);
    Task<bool> RefundAsync(string paymentIntentId, CancellationToken ct = default);
}

// Server-side Stripe card charges. The browser collects the card with Stripe Elements and confirms the
// PaymentIntent; this service creates the intent, verifies the confirmed payment, and issues refunds.
// Money is collected on the platform account. Sitters do not need a Stripe account: the recorded payment
// is what appears in their in-app earnings and notifications.
// When Stripe keys are absent the app keeps its local demo-payment behaviour.
public sealed class StripePaymentService : IStripePaymentService
{
    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg", "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf"
    };

    private readonly StripeOptions _options;
    private readonly StripeClient? _client;
    private readonly ILogger<StripePaymentService> _logger;

    public StripePaymentService(IOptions<StripeOptions> options, ILogger<StripePaymentService> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (_options.IsConfigured) _client = new StripeClient(_options.SecretKey);
    }

    public bool IsConfigured => _client is not null;
    public string PublishableKey => _options.PublishableKey;

    public async Task<StripeIntentResult> CreateIntentAsync(decimal amount, string currency, Guid bookingId, Guid payerId, string description, CancellationToken ct = default)
    {
        if (_client is null) return new(false, null, "Card payments are not configured.");
        if (amount <= 0) return new(false, null, "There is nothing left to pay on this booking.");
        var code = currency.ToLowerInvariant();
        try
        {
            var intent = await new PaymentIntentService(_client).CreateAsync(new PaymentIntentCreateOptions
            {
                Amount = ToMinorUnits(amount, code),
                Currency = code,
                Description = description.Length > 500 ? description[..500] : description,
                AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions { Enabled = true },
                Metadata = new Dictionary<string, string>
                {
                    ["bookingId"] = bookingId.ToString(),
                    ["payerId"] = payerId.ToString()
                }
            // Stable per booking and amount: a retried request reuses the same intent instead of charging twice.
            }, new RequestOptions { IdempotencyKey = $"pay-{bookingId}-{ToMinorUnits(amount, code)}" }, ct);
            return new(true, intent.ClientSecret, null);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Stripe could not create a payment intent for booking {BookingId}.", bookingId);
            return new(false, null, StripeMessage(ex));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Stripe could not be reached while creating a payment intent for booking {BookingId}.", bookingId);
            return new(false, null, "The payment service could not be reached. Try again in a moment.");
        }
    }

    public async Task<StripeVerificationResult> VerifyAsync(string paymentIntentId, Guid bookingId, Guid payerId, decimal amount, string currency, CancellationToken ct = default)
    {
        if (_client is null) return new(false, "Card payments are not configured.");
        try
        {
            var intent = await new PaymentIntentService(_client).GetAsync(paymentIntentId, cancellationToken: ct);
            if (!string.Equals(intent.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
                return new(false, "The card payment was not completed. Try again.");
            if (intent.Amount != ToMinorUnits(amount, currency))
                return new(false, "The paid amount does not match this booking. Refresh and try again.");
            if (!string.Equals(intent.Currency, currency.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                return new(false, "The payment currency does not match this booking. Refresh and try again.");
            if (intent.Metadata.GetValueOrDefault("bookingId") != bookingId.ToString() ||
                intent.Metadata.GetValueOrDefault("payerId") != payerId.ToString())
                return new(false, "This payment does not belong to this booking.");
            return new(true, null);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Stripe could not verify payment {PaymentIntentId}.", paymentIntentId);
            return new(false, StripeMessage(ex));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Stripe could not be reached while verifying payment {PaymentIntentId}.", paymentIntentId);
            return new(false, "The payment service could not be reached. Try again in a moment.");
        }
    }

    public async Task<bool> RefundAsync(string paymentIntentId, CancellationToken ct = default)
    {
        if (_client is null) return false;
        try
        {
            await new RefundService(_client).CreateAsync(new RefundCreateOptions
            {
                PaymentIntent = paymentIntentId,
                Reason = "requested_by_customer"
            }, new RequestOptions { IdempotencyKey = "refund-" + paymentIntentId }, ct);
            return true;
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Stripe could not refund payment {PaymentIntentId}.", paymentIntentId);
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Stripe could not be reached while refunding payment {PaymentIntentId}.", paymentIntentId);
            return false;
        }
    }

    // Stripe amounts are in the currency's smallest unit (cents for USD).
    public static long ToMinorUnits(decimal amount, string currency) =>
        ZeroDecimalCurrencies.Contains(currency) ? (long)decimal.Round(amount, 0) : (long)decimal.Round(amount * 100m, 0);
    public static decimal FromMinorUnits(long amount, string currency) =>
        ZeroDecimalCurrencies.Contains(currency) ? amount : amount / 100m;

    private static string StripeMessage(StripeException ex) =>
        ex.StripeError?.Message is { Length: > 0 } message ? message : "The card payment could not be completed.";
}
