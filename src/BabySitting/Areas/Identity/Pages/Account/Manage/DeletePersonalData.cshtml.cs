using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using BabySitting.Models;

namespace BabySitting.Areas.Identity.Pages.Account.Manage;

[Authorize]
public sealed class DeletePersonalDataModel(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public sealed class InputModel
    {
        [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    }
    public void OnGet() { }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await users.CheckPasswordAsync(user, Input.Password))
        {
            ModelState.AddModelError("Input.Password", "Incorrect password.");
            return Page();
        }
        try
        {
            var result = await users.DeleteAsync(user);
            if (!result.Succeeded)
            {
                ModelState.AddModelError(string.Empty, "Unable to delete this account. Please try again.");
                return Page();
            }
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 547 })
        {
            ModelState.AddModelError(string.Empty, "This account has care records that must be retained. Contact your platform manager for account closure.");
            return Page();
        }
        await signIn.SignOutAsync();
        return Redirect("~/");
    }
}
