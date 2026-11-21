using Microsoft.AspNetCore.Identity;
using PetSitting.Models;

namespace PetSitting.Services.Auth;

// Where to go right after signing in. Platform managers always start on their home page (Dashboard -> Overview),
// even when the login page was opened from another page: going back to a sitter or owner page would only show
// "Access denied". Other users go back to the page they came from, unless it is a manager-only page.
public static class LoginDestination
{
    public const string Home = "/Dashboard";
    private static readonly string[] ManagerPages = ["/ManagerReports", "/AccountClosures", "/Payouts", "/SitterVerifications", "/ManagerUsers", "/Dashboard/Manager"];

    public static string For(bool manager, string returnUrl) =>
        manager || ManagerPages.Any(page => returnUrl.StartsWith(page, StringComparison.OrdinalIgnoreCase)) ? Home : returnUrl;

    public static async Task<string> ForAsync(UserManager<ApplicationUser> users, ApplicationUser? user, string returnUrl) =>
        For(user is not null && await users.IsInRoleAsync(user, Roles.PlatformManager), returnUrl);
}
