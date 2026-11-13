using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PetSitting.Models;
using PetSitting.Services.Dashboard;

namespace PetSitting.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DashboardController(IDashboardService dashboard, UserManager<ApplicationUser> users) : Controller
{
    public IActionResult Index() => User.IsInRole(Roles.Customer) ? RedirectToAction(nameof(Customer))
        : User.IsInRole(Roles.Sitter) ? RedirectToAction(nameof(Sitter))
        : User.IsInRole(Roles.PlatformManager) ? RedirectToAction(nameof(Manager)) : View();

    [Authorize(Roles = Roles.Customer)]
    public async Task<IActionResult> Customer(CancellationToken ct)
    {
        var user = await users.GetUserAsync(User);
        return user is null ? Challenge() : View("Customer", await dashboard.LoadAsync(user, false, ct));
    }

    [Authorize(Roles = Roles.Sitter)]
    public async Task<IActionResult> Sitter(CancellationToken ct)
    {
        var user = await users.GetUserAsync(User);
        return user is null ? Challenge() : View("Sitter", await dashboard.LoadAsync(user, true, ct));
    }

    [Authorize(Roles = Roles.PlatformManager)]
    public IActionResult Manager() => View("Role", "Platform manager");
}
