using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using PetSitting.Models;

namespace PetSitting.Areas.Identity.Pages.Account;

[AllowAnonymous]
public sealed class ConfirmEmailModel(UserManager<ApplicationUser> users) : PageModel
{
    public bool Confirmed { get; private set; }
    public string? ReturnUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? userId, string? code, string? returnUrl = null)
    {
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        if (!Guid.TryParse(userId, out _) || string.IsNullOrWhiteSpace(code)) return Page();
        var user = await users.FindByIdAsync(userId);
        if (user is null) return Page();
        try
        {
            var token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
            Confirmed = (await users.ConfirmEmailAsync(user, token)).Succeeded;
        }
        catch (FormatException)
        {
            return Page();
        }
        return Page();
    }
}
