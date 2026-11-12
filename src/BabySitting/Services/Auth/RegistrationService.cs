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
}
