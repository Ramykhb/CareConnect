using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Identity;
using PetSitting.Models;
using PetSitting.Services.Auth;

namespace PetSitting.Areas.Identity.Pages.Account;

[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RegisterModel(RegistrationService registration, UserManager<ApplicationUser> users, IEmailSender emailSender) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public string? ReturnUrl { get; set; }
    public sealed class InputModel
    {
        [Required, StringLength(100), RegularExpression(@".*\S.*", ErrorMessage = "Enter a first name.")]
        [Display(Name = "First name")] public string FirstName { get; set; } = string.Empty;
        [Required, StringLength(100), RegularExpression(@".*\S.*", ErrorMessage = "Enter a last name.")]
        [Display(Name = "Last name")] public string LastName { get; set; } = string.Empty;
        [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = string.Empty;
        [Required, StringLength(100, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
        [DataType(DataType.Password), Compare(nameof(Password)), Display(Name = "Confirm password")]
        public string ConfirmPassword { get; set; } = string.Empty;
        [Required, RegularExpression("^(Customer|Sitter)$"), Display(Name = "Account type")]
        public string AccountType { get; set; } = "Customer";
    }
    public void OnGet(string? returnUrl = null, string? accountType = null)
    {
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        Input.AccountType = accountType == "Sitter" ? "Sitter" : "Customer";
    }
    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        if (!ModelState.IsValid) return Page();
        var (result, user) = await registration.RegisterAsync(Input.Email, Input.Password, Input.FirstName, Input.LastName, Input.AccountType);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Code.StartsWith("Duplicate", StringComparison.Ordinal)
                    ? "Unable to create this account. Try signing in or resetting your password." : error.Description);
            return Page();
        }
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GenerateEmailConfirmationTokenAsync(user!)));
        // Build links from a configured origin to prevent Host-header poisoning.
        var origin = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Auth:PublicOrigin"]!;
        var callback = origin.TrimEnd('/') + Url.Page("/Account/ConfirmEmail", null, new { area = "Identity", userId = user!.Id, code, returnUrl = ReturnUrl });
        try
        {
            await emailSender.SendEmailAsync(user.Email!, "Confirm your CareConnect account",
                $"Confirm your account by <a href='{HtmlEncoder.Default.Encode(callback)}'>clicking here</a>.");
        }
        catch (Exception exception) when (exception is System.Net.Mail.SmtpException)
        {
            HttpContext.RequestServices.GetRequiredService<ILogger<RegisterModel>>().LogWarning("Confirmation email delivery failed; user can resend confirmation.");
            TempData["StatusMessage"] = "Your account was created, but email delivery failed. Use Resend email confirmation on the login page.";
        }
        TempData[RegisterConfirmationModel.EmailKey] = user.Email;
        var retry = HttpContext.RequestServices.GetRequiredService<PasswordResetCooldown>().Reserve(user.Email!, "email-confirmation");
        TempData[RegisterConfirmationModel.RetryKey] = retry.RetryAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return RedirectToPage("RegisterConfirmation", new { returnUrl = ReturnUrl });
    }
}
