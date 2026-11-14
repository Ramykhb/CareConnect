using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using BabySitting.Models;
using BabySitting.Services.Auth;

namespace BabySitting.Areas.Identity.Pages.Account;

[AllowAnonymous]
public sealed class RegisterConfirmationModel(
    UserManager<ApplicationUser> users, IEmailSender emailSender, PasswordResetCooldown cooldown,
    IConfiguration configuration, IWebHostEnvironment environment, ILogger<RegisterConfirmationModel> logger) : PageModel
{
    public const string EmailKey = "Registration.Email";
    public const string RetryKey = "Registration.RetryAt";
    public string? Email => TempData.Peek(EmailKey) as string;
    public long RetryAt => long.TryParse(TempData.Peek(RetryKey) as string, out var value) ? value : 0;
    public string? ReturnUrl { get; private set; }
    public string? InboxUrl { get; private set; }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        if (Uri.TryCreate(configuration["Email:InboxUrl"], UriKind.Absolute, out var inbox)
            && (inbox.Scheme == "https" || (environment.IsDevelopment() && inbox.IsLoopback && inbox.Scheme == "http")))
            InboxUrl = inbox.AbsoluteUri;
    }

    public async Task<IActionResult> OnPostResendAsync(string? returnUrl = null)
    {
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        var email = Email;
        if (string.IsNullOrWhiteSpace(email)) return RedirectToPage("ResendEmailConfirmation");
        var reservation = cooldown.Reserve(email, "email-confirmation");
        TempData[RetryKey] = reservation.RetryAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (reservation.Allowed)
        {
            var user = await users.FindByEmailAsync(email);
            if (user is not null && user.IsActive && !await users.IsEmailConfirmedAsync(user))
            {
                var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GenerateEmailConfirmationTokenAsync(user)));
                var callback = configuration["Auth:PublicOrigin"]!.TrimEnd('/') + Url.Page("/Account/ConfirmEmail", null,
                    new { area = "Identity", userId = user.Id, code, returnUrl = ReturnUrl });
                try
                {
                    await emailSender.SendEmailAsync(email, "Confirm your CareConnect account",
                        $"Confirm your account by <a href='{HtmlEncoder.Default.Encode(callback)}'>clicking here</a>.");
                }
                catch (System.Net.Mail.SmtpException)
                {
                    logger.LogWarning("Confirmation email delivery failed; user can resend confirmation.");
                    TempData["StatusMessage"] = "We couldn’t send the verification email. Please try again shortly.";
                }
            }
        }
        return RedirectToPage(new { returnUrl = ReturnUrl });
    }
}
