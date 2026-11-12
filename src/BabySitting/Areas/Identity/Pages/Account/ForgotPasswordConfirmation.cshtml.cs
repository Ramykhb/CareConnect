using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using BabySitting.Services.Auth;

namespace BabySitting.Areas.Identity.Pages.Account;

[AllowAnonymous]
public sealed class ForgotPasswordConfirmationModel(
    PasswordResetService passwordReset, IConfiguration configuration, IWebHostEnvironment environment) : PageModel
{
    public const string EmailKey = "PasswordReset.Email";
    public const string RetryKey = "PasswordReset.RetryAt";
    public string? Email => TempData.Peek(EmailKey) as string;
    public long RetryAt => long.TryParse(TempData.Peek(RetryKey) as string, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
    public string Expiry => $"{passwordReset.TokenLifetime.TotalHours:0.##} hours";
    public string? InboxUrl { get; private set; }

    public void OnGet()
    {
        // Configured by the operator, never derived from an untrusted redirect parameter.
        if (Uri.TryCreate(configuration["Email:InboxUrl"], UriKind.Absolute, out var inbox)
            && (inbox.Scheme == "https" || (environment.IsDevelopment() && inbox.IsLoopback && inbox.Scheme == "http")))
            InboxUrl = inbox.AbsoluteUri;
    }
    public async Task<IActionResult> OnPostResendAsync()
    {
        var email = Email;
        if (string.IsNullOrWhiteSpace(email)) return RedirectToPage("ForgotPassword");
        var retryAt = await passwordReset.RequestAsync(email);
        TempData[EmailKey] = email;
        TempData[RetryKey] = retryAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return RedirectToPage();
    }
}
