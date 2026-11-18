using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using BabySitting.Models;

namespace BabySitting.Services.Localization;

public static class UserLanguage
{
    public const string ClaimType = "careconnect:lang";
    public static readonly string[] Supported = ["en", "ar", "fr"];

    // Only the display language changes; number and date formats stay the same so form input keeps parsing identically.
    public static IApplicationBuilder UseUserLanguage(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var lang = context.User.FindFirst(ClaimType)?.Value;
        if (lang is not null && lang != "en" && Supported.Contains(lang)) CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(lang);
        await next();
    });
}

// Adds the saved language to the sign-in cookie, so no database read is needed per request.
// It refreshes on sign-in and whenever settings call RefreshSignInAsync.
public sealed class LanguageClaimsFactory(UserManager<ApplicationUser> users, RoleManager<IdentityRole<Guid>> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole<Guid>>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(UserLanguage.ClaimType, UserLanguage.Supported.Contains(user.PreferredLanguage) ? user.PreferredLanguage : "en"));
        return identity;
    }
}
