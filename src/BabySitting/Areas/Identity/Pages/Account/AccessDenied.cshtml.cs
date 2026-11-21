using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BabySitting.Areas.Identity.Pages.Account;

// Shown when a signed-in person opens a page meant for another kind of account (for example a manager opening a
// sitter page). It explains why and links back to their own home page instead of leaving them stuck.
[AllowAnonymous]
public sealed class AccessDeniedModel : PageModel
{
    public void OnGet() { }
}
