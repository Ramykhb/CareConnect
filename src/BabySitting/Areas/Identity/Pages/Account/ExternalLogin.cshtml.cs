using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using BabySitting.Models;
using BabySitting.Services.Auth;

namespace BabySitting.Areas.Identity.Pages.Account;

[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ExternalLoginModel(SignInManager<ApplicationUser> signIn, RegistrationService registration) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public string ReturnUrl { get; private set; } = "/Dashboard";
    public string Email { get; private set; } = "";
    public sealed class InputModel
    {
        [Required, StringLength(100), RegularExpression(@".*\S.*")]
        [Display(Name = "First name")] public string FirstName { get; set; } = "";
        [Required, StringLength(100), RegularExpression(@".*\S.*")]
        [Display(Name = "Last name")] public string LastName { get; set; } = "";
        [Required, RegularExpression("^(Customer|Sitter)$")]
        [Display(Name = "Account type")] public string AccountType { get; set; } = "Customer";
    }

    public IActionResult OnGet() => RedirectToPage("Login");

    public async Task<IActionResult> OnPostChallengeAsync(string? returnUrl = null)
    {
        if (!(await signIn.GetExternalAuthenticationSchemesAsync()).Any(s => s.Name == "Google")) return NotFound();
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        ReturnUrl = LocalReturn(returnUrl);
        var callback = Url.Page("ExternalLogin", "Callback", new { returnUrl = ReturnUrl });
        return Challenge(signIn.ConfigureExternalAuthenticationProperties("Google", callback), "Google");
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? returnUrl = null)
    {
        ReturnUrl = LocalReturn(returnUrl);
        var info = await signIn.GetExternalLoginInfoAsync();
        if (!ValidGoogle(info)) return await FailedAsync();
        var result = await signIn.ExternalLoginSignInAsync("Google", info!.ProviderKey, isPersistent: false, bypassTwoFactor: false);
        if (result.Succeeded || result.RequiresTwoFactor)
            ReturnUrl = await LoginDestination.ForAsync(signIn.UserManager, await signIn.UserManager.FindByLoginAsync("Google", info.ProviderKey), ReturnUrl);
        if (result.Succeeded) return await FinishAsync();
        if (result.RequiresTwoFactor) return RedirectToPage("LoginWith2fa", new { returnUrl = ReturnUrl, rememberMe = false });
        if (result.IsLockedOut) return RedirectToPage("Lockout");
        // A linked but inactive/unconfirmed account must never fall through to registration.
        if (await signIn.UserManager.FindByLoginAsync("Google", info.ProviderKey) is not null) return await FailedAsync();
        Email = info.Principal.FindFirstValue(ClaimTypes.Email)!;
        if (await signIn.UserManager.FindByEmailAsync(Email) is not null) return await ExistingAccountAsync();
        Input.FirstName = info.Principal.FindFirstValue(ClaimTypes.GivenName) ?? "";
        Input.LastName = info.Principal.FindFirstValue(ClaimTypes.Surname) ?? "";
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmationAsync(string? returnUrl = null)
    {
        ReturnUrl = LocalReturn(returnUrl);
        var info = await signIn.GetExternalLoginInfoAsync();
        if (!ValidGoogle(info)) return await FailedAsync();
        Email = info!.Principal.FindFirstValue(ClaimTypes.Email)!;
        if (!ModelState.IsValid) return Page();
        if (await signIn.UserManager.FindByEmailAsync(Email) is not null) return await ExistingAccountAsync();
        var (result, _) = await registration.RegisterGoogleAsync(info, Input.FirstName, Input.LastName, Input.AccountType);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "Unable to create this account. If you already registered, sign in and link Google in account settings.");
            return Page();
        }
        var login = await signIn.ExternalLoginSignInAsync("Google", info.ProviderKey, isPersistent: false, bypassTwoFactor: false);
        ReturnUrl = LoginDestination.For(false, ReturnUrl);
        return login.Succeeded ? await FinishAsync() : await FailedAsync();
    }

    private string LocalReturn(string? url) => Url.IsLocalUrl(url) ? url! : "/Dashboard";
    private static bool ValidGoogle(ExternalLoginInfo? info) => info?.LoginProvider == "Google"
        && string.Equals(info.Principal.FindFirstValue("google:email_verified"), "true", StringComparison.OrdinalIgnoreCase)
        && info.Principal.FindFirstValue(ClaimTypes.Email) is { Length: > 0 and <= 256 } email
        && new EmailAddressAttribute().IsValid(email);
    private async Task<IActionResult> FinishAsync()
    {
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        return LocalRedirect(ReturnUrl);
    }
    private async Task<IActionResult> FailedAsync()
    {
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        return RedirectToPage("Login", new { googleError = true });
    }
    private async Task<IActionResult> ExistingAccountAsync()
    {
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        TempData["GoogleLinkMessage"] = "An account with this email already exists. Sign in using your existing method, then open Account settings → Google & linked accounts to link Google securely.";
        return RedirectToPage("Login");
    }
}
