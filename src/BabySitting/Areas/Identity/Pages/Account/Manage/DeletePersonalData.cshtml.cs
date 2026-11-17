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
public sealed class DeletePersonalDataModel(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, BabySitting.Services.Accounts.IAccountClosureService closures) : PageModel
{
    // Set when deletion is blocked by care records: the page then offers a closure request instead.
    public bool NeedsManager { get; private set; }
    public AccountClosureRequest? Pending { get; private set; }
    [BindProperty] public string? Reason { get; set; }
    [TempData] public string? StatusMessage { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new();
    public sealed class InputModel
    {
        [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    }
    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        Pending = await closures.PendingForUserAsync(user.Id);
        return await users.HasPasswordAsync(user) ? Page() : RedirectToPage("SetPassword");
    }
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
            ModelState.AddModelError(string.Empty, "This account has care records that must be kept, so it can't be deleted directly. Ask a platform manager to close it below.");
            NeedsManager = true;
            return Page();
        }
        await signIn.SignOutAsync();
        return Redirect("~/");
    }

    // POST: ask a platform manager to close the account (password confirmed again).
    public async Task<IActionResult> OnPostRequestClosureAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        ModelState.Remove("Reason");
        if (!ModelState.IsValid) { NeedsManager = true; return Page(); }
        if (!await users.CheckPasswordAsync(user, Input.Password))
        {
            ModelState.AddModelError("Input.Password", "Incorrect password.");
            NeedsManager = true;
            return Page();
        }
        var result = await closures.RequestAsync(user.Id, Reason);
        StatusMessage = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostWithdrawClosureAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        ModelState.Clear();
        StatusMessage = (await closures.WithdrawAsync(user.Id)).Message;
        return RedirectToPage();
    }
}
