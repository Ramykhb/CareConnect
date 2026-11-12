using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using BabySitting.Models;

namespace BabySitting.Services.Auth;

public sealed class ActiveUserSignInManager(
    UserManager<ApplicationUser> users, IHttpContextAccessor context,
    IUserClaimsPrincipalFactory<ApplicationUser> claims, IOptions<IdentityOptions> options,
    ILogger<SignInManager<ApplicationUser>> logger, IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation)
    : SignInManager<ApplicationUser>(users, context, claims, options, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(ApplicationUser user)
    {
        if (!user.IsActive || !await base.CanSignInAsync(user)) return false;
        // Initialize the key before issuing a cookie: Identity's authenticator setup would
        // otherwise generate it during GET and invalidate a strictly validated session.
        if (await UserManager.GetAuthenticatorKeyAsync(user) is null)
        {
            var result = await UserManager.ResetAuthenticatorKeyAsync(user);
            if (!result.Succeeded) return false;
        }
        return true;
    }
}
