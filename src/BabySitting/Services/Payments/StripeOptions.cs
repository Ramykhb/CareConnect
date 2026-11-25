namespace BabySitting.Services.Payments;

// Stripe settings loaded from configuration ("Stripe:SecretKey"/"Stripe:PublishableKey"/"Stripe:WebhookSecret")
// or the matching STRIPE_* environment variables.
public sealed class StripeOptions
{
    public string SecretKey { get; set; } = "";
    public string PublishableKey { get; set; } = "";
    // Endpoint signing secret from `stripe listen` or the Stripe dashboard webhook settings.
    public string WebhookSecret { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(PublishableKey);
}
