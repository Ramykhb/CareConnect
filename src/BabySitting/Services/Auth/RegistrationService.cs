using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;

namespace BabySitting.Services.Auth;

public sealed class RegistrationService(BabySittingDbContext db, UserManager<ApplicationUser> users)
{
    public async Task<(IdentityResult Result, ApplicationUser? User)> RegisterAsync(
        string email, string password, string firstName, string lastName, string accountType)
    {
        string? role = accountType switch
        {
            "Customer" => Roles.Customer,
            "Sitter" => Roles.Sitter,
            _ => null
        };
        if (role is null)
            return (IdentityResult.Failed(new IdentityError { Description = "Select a valid account type." }), null);

        // A failed role assignment must not leave a roleless account behind.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email.Trim(), Email = email.Trim(),
                FirstName = firstName.Trim(), LastName = lastName.Trim() };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded) return (result, (ApplicationUser?)null);
            result = await users.AddToRoleAsync(user, role);
            if (!result.Succeeded) return (result, (ApplicationUser?)null);
            await transaction.CommitAsync();
            return (IdentityResult.Success, (ApplicationUser?)user);
        });
    }
    // The email and provider key come only from the protected external cookie, never form fields.
    public async Task<(IdentityResult Result, ApplicationUser? User)> RegisterGoogleAsync(
        ExternalLoginInfo info, string firstName, string lastName, string accountType)
    {
        var email = info.Principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        var role = accountType switch { "Customer" => Roles.Customer, "Sitter" => Roles.Sitter, _ => null };
        if (info.LoginProvider != "Google" || !string.Equals(info.Principal.FindFirst("google:email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(email) || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email)
            || email.Length > 256 || role is null || string.IsNullOrWhiteSpace(firstName) || firstName.Length > 100
            || string.IsNullOrWhiteSpace(lastName) || lastName.Length > 100)
            return (IdentityResult.Failed(new IdentityError { Description = "Google sign-up details are invalid. Start again from the login page." }), null);

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email,
                EmailConfirmed = true, FirstName = firstName.Trim(), LastName = lastName.Trim() };
            var result = await users.CreateAsync(user);
            if (!result.Succeeded) return (result, (ApplicationUser?)null);
            result = await users.AddToRoleAsync(user, role);
            if (!result.Succeeded) return (result, (ApplicationUser?)null);
            result = await users.AddLoginAsync(user, info);
            if (!result.Succeeded) return (result, (ApplicationUser?)null);
            await transaction.CommitAsync();
            return (IdentityResult.Success, (ApplicationUser?)user);
        });
    }

}
