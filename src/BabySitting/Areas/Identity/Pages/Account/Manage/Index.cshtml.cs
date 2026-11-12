using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using BabySitting.Models;

namespace BabySitting.Areas.Identity.Pages.Account.Manage;

[Authorize]
public sealed class IndexModel(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    [TempData] public string? StatusMessage { get; set; }
    public sealed class InputModel
    {
        [Required, StringLength(100), RegularExpression(@".*\S.*"), Display(Name = "First name")]
        public string FirstName { get; set; } = string.Empty;
        [Required, StringLength(100), RegularExpression(@".*\S.*"), Display(Name = "Last name")]
        public string LastName { get; set; } = string.Empty;
        [StringLength(150)] public string? City { get; set; }
        [Phone, StringLength(30), Display(Name = "Phone number")] public string? PhoneNumber { get; set; }
    }
    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        Input = new() { FirstName = user.FirstName, LastName = user.LastName, City = user.City, PhoneNumber = user.PhoneNumber };
        return Page();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        user.FirstName = Input.FirstName.Trim();
        user.LastName = Input.LastName.Trim();
        user.City = Input.City?.Trim();
        if (user.PhoneNumber != Input.PhoneNumber?.Trim()) user.PhoneNumberConfirmed = false;
        user.PhoneNumber = Input.PhoneNumber?.Trim();
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }
        await signIn.RefreshSignInAsync(user);
        StatusMessage = "Your profile has been updated.";
        return RedirectToPage();
    }
}
