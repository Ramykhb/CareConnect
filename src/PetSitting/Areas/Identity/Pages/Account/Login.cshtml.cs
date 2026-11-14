using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetSitting.Models;

namespace PetSitting.Areas.Identity.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel(SignInManager<ApplicationUser> signIn) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public string ReturnUrl { get; set; } = "/Dashboard";
    public sealed class InputModel
    {
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
        [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
        [Display(Name = "Remember me")] public bool RememberMe { get; set; }
    }
    public void OnGet(string? returnUrl = null) => ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/Dashboard";
    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        OnGet(returnUrl);
        if (!ModelState.IsValid) return Page();
        var result = await signIn.PasswordSignInAsync(Input.Email.Trim(), Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded) return LocalRedirect(ReturnUrl);
        if (result.RequiresTwoFactor) return RedirectToPage("LoginWith2fa", new { returnUrl = ReturnUrl, rememberMe = Input.RememberMe });
        if (result.IsLockedOut) return RedirectToPage("Lockout");
        if (result.IsNotAllowed)
        {
            var users = signIn.UserManager;
            var user = await users.FindByEmailAsync(Input.Email.Trim());
            if (user is { IsActive: true } && !await users.IsEmailConfirmedAsync(user))
            {
                // Identity checks confirmation before the password. Verify credentials here
                // before showing the account's verification screen, without issuing a cookie.
                if (await users.IsLockedOutAsync(user)) return RedirectToPage("Lockout");
                if (await users.CheckPasswordAsync(user, Input.Password))
                {
                    if (!string.Equals(TempData.Peek(RegisterConfirmationModel.EmailKey) as string, user.Email, StringComparison.OrdinalIgnoreCase))
                        TempData.Remove(RegisterConfirmationModel.RetryKey);
                    TempData[RegisterConfirmationModel.EmailKey] = user.Email;
                    return RedirectToPage("RegisterConfirmation", new { returnUrl = ReturnUrl });
                }
                if (users.SupportsUserLockout)
                {
                    await users.AccessFailedAsync(user);
                    if (await users.IsLockedOutAsync(user)) return RedirectToPage("Lockout");
                }
            }
        }
        ModelState.AddModelError(string.Empty, "Unable to sign in. Check your credentials and confirm your email before signing in.");
        return Page();
    }
}
