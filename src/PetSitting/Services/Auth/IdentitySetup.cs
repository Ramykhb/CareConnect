using Microsoft.AspNetCore.Identity;
using PetSitting.Models;

namespace PetSitting.Services.Auth;

public static class IdentitySetup
{
    // Explicit CLI operation; never a public HTTP endpoint or an automatic role upgrade.
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var name in new[] { Roles.Customer, Roles.Sitter, Roles.PlatformManager })
            if (!await roles.RoleExistsAsync(name))
                Ensure(await roles.CreateAsync(new IdentityRole<Guid>(name)));

        var email = configuration["BootstrapAdmin:Email"];
        if (string.IsNullOrWhiteSpace(email)) return;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        if (user is not null)
        {
            if (!await users.IsInRoleAsync(user, Roles.PlatformManager))
                throw new InvalidOperationException("Refusing to promote an existing non-manager account. Choose a new manager email.");
            return;
        }
        var password = configuration["BootstrapAdmin:Password"]
            ?? throw new InvalidOperationException("Set BootstrapAdmin:Password in user secrets before provisioning a manager.");
        user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email,
            EmailConfirmed = true, FirstName = "Platform", LastName = "Manager" };
        Ensure(await users.CreateAsync(user, password));
        var assigned = await users.AddToRoleAsync(user, Roles.PlatformManager);
        if (!assigned.Succeeded)
        {
            Ensure(await users.DeleteAsync(user));
            Ensure(assigned);
        }
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Description)));
    }
}
