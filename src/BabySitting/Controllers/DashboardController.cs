using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BabySitting.Models;

namespace BabySitting.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DashboardController : Controller
{
    public IActionResult Index() => View();
    [Authorize(Roles = Roles.Customer)] public IActionResult Customer() => View("Role", "Customer");
    [Authorize(Roles = Roles.Sitter)] public IActionResult Sitter() => View("Role", "Sitter");
    [Authorize(Roles = Roles.PlatformManager)] public IActionResult Manager() => View("Role", "Platform manager");
}
