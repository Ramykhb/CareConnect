using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using BabySitting.Services.Auth;

namespace BabySitting.Areas.Identity.Pages.Account;

[AllowAnonymous]
public sealed class ForgotPasswordModel(PasswordResetService passwordReset) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public sealed class InputModel
    {
        [Required, EmailAddress, StringLength(256)]
        public string Email { get; set; } = string.Empty;
    }
    public void OnGet() { }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var email = Input.Email.Trim();
        var retryAt = await passwordReset.RequestAsync(email);
        TempData[ForgotPasswordConfirmationModel.EmailKey] = email;
        TempData[ForgotPasswordConfirmationModel.RetryKey] = retryAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return RedirectToPage("ForgotPasswordConfirmation");
    }
}
