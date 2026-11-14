using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using BabySitting.Models;

namespace BabySitting.Services.Auth;

// Singleton: reserve before sending so concurrent requests cannot send duplicate emails.
public sealed class PasswordResetCooldown(IMemoryCache cache, TimeProvider clock)
{
    private readonly object gate = new();
    public (bool Allowed, DateTimeOffset RetryAt) Reserve(string email, string purpose = "password-reset")
    {
        var key = purpose + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToUpperInvariant())));
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (cache.TryGetValue<DateTimeOffset>(key, out var retryAt) && retryAt > now)
                return (false, retryAt);
            retryAt = now.AddSeconds(45);
            cache.Set(key, retryAt, TimeSpan.FromSeconds(45));
            return (true, retryAt);
        }
    }
}

public sealed class PasswordResetService(
    UserManager<ApplicationUser> users, IEmailSender emailSender, PasswordResetCooldown cooldown,
    IOptions<AuthOriginOptions> origin, IOptions<DataProtectionTokenProviderOptions> tokens,
    ILogger<PasswordResetService> logger)
{
    public TimeSpan TokenLifetime => tokens.Value.TokenLifespan;

    public async Task<DateTimeOffset> RequestAsync(string email)
    {
        var reservation = cooldown.Reserve(email);
        if (!reservation.Allowed) return reservation.RetryAt;
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || !user.IsActive || !await users.IsEmailConfirmedAsync(user))
            return reservation.RetryAt;

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var callback = QueryHelpers.AddQueryString(origin.Value.PublicOrigin.TrimEnd('/') + "/Identity/Account/ResetPassword", "code", code);
        try
        {
            await emailSender.SendEmailAsync(user.Email!, "Reset your CareConnect password",
                $"<p>Reset your CareConnect password by <a href='{HtmlEncoder.Default.Encode(callback)}'>following this secure link</a>.</p>" +
                $"<p>This link expires in {TokenLifetime.TotalHours:0.##} hours. If you did not request a reset, you can ignore this email.</p>");
        }
        catch (SmtpException)
        {
            // Keep the public response identical; do not log addresses or reset tokens.
            logger.LogWarning("Password reset email delivery failed. The request can be retried after the cooldown.");
        }
        return reservation.RetryAt;
    }
}
