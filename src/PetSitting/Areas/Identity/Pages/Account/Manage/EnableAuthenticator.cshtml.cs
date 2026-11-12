using System.ComponentModel.DataAnnotations;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetSitting.Models;

namespace PetSitting.Areas.Identity.Pages.Account.Manage;

[Authorize]
public sealed class EnableAuthenticatorModel(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public string SharedKey { get; set; } = string.Empty;
    [TempData] public string[] RecoveryCodes { get; set; } = [];
    public sealed class InputModel
    {
        [Required, StringLength(7, MinimumLength = 6), Display(Name = "Verification code")]
        public string Code { get; set; } = string.Empty;
    }
    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        await LoadKeyAsync(user);
        return Page();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        await LoadKeyAsync(user);
        if (!ModelState.IsValid) return Page();
        var code = Input.Code.Replace(" ", string.Empty).Replace("-", string.Empty);
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            ModelState.AddModelError("Input.Code", "The verification code is invalid.");
            return Page();
        }
        var result = await users.SetTwoFactorEnabledAsync(user, true);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "Unable to enable your authenticator. Please try again.");
            return Page();
        }
        if (await users.CountRecoveryCodesAsync(user) == 0)
            RecoveryCodes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToArray();
        // Keep the current session valid after the security stamp changes.
        await signIn.RefreshSignInAsync(user);
        return RecoveryCodes.Length > 0 ? RedirectToPage("ShowRecoveryCodes") : RedirectToPage("TwoFactorAuthentication");
    }
    private async Task LoadKeyAsync(ApplicationUser user)
    {
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            var result = await users.ResetAuthenticatorKeyAsync(user);
            if (!result.Succeeded) throw new InvalidOperationException("Unable to initialize authenticator.");
            key = await users.GetAuthenticatorKeyAsync(user);
            await signIn.RefreshSignInAsync(user);
        }
        SharedKey = key!;
    }
}
