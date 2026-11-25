using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PetSitting.Services.Payments;

namespace PetSitting.Controllers;

// Stripe signs the raw request body, so this endpoint is anonymous and has no antiforgery requirement.
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("Stripe/Webhook")]
public sealed class StripeWebhookController(StripeWebhookHandler handler) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Handle(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync(ct);
        var result = await handler.HandleAsync(json, Request.Headers["Stripe-Signature"].ToString(), ct);
        return StatusCode(result.StatusCode, new { status = result.Message });
    }
}
