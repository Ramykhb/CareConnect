using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;

namespace PetSitting.Services.Auth;

public sealed class RegistrationService(PetSittingDbContext db, UserManager<ApplicationUser> users)
{
    public async Task<(IdentityResult Result, ApplicationUser? User)> RegisterAsync(
        string email, string password, string firstName, string lastName, string accountType)
    {
        string[] roles = accountType switch
        {
            "Customer" => [Roles.Customer],
            "Sitter" => [Roles.Sitter],
            "Both" => [Roles.Customer, Roles.Sitter],
            _ => []
        };
        if (roles.Length == 0)
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
            result = await users.AddToRolesAsync(user, roles);
            if (!result.Succeeded) return (result, (ApplicationUser?)null);
            await transaction.CommitAsync();
            return (IdentityResult.Success, (ApplicationUser?)user);
        });
    }
}
