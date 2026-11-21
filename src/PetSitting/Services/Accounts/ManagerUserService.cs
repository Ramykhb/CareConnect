using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Auth;

namespace PetSitting.Services.Accounts;

public sealed record ManagedUser(Guid Id, string Name, string? Email, string Role, bool IsActive, bool EmailConfirmed, string Version);
public sealed record ManagedUserPage(IReadOnlyList<ManagedUser> Users, int Total, int Page, int PageSize, string? Search, string Role, string Status)
{
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}
public sealed record ManagerWriteResult(bool Succeeded, string Message, bool NotFound = false);

public sealed class AddManagerForm
{
    [Required, StringLength(100), RegularExpression(@".*\S.*"), Display(Name = "First name")] public string FirstName { get; set; } = "";
    [Required, StringLength(100), RegularExpression(@".*\S.*"), Display(Name = "Last name")] public string LastName { get; set; } = "";
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
}

// Platform managers look after accounts: find users, switch an account off or on, and add other managers.
public interface IManagerUserService
{
    Task<ManagedUserPage> ListAsync(string? search, string? role, string? status, int page, CancellationToken ct = default);
    Task<ManagerWriteResult> SetActiveAsync(Guid managerId, Guid userId, bool active, string? version, CancellationToken ct = default);
    Task<ManagerWriteResult> AddManagerAsync(AddManagerForm form, CancellationToken ct = default);
}

public sealed class ManagerUserService(PetSittingDbContext db, UserManager<ApplicationUser> users, IEmailSender email,
    IOptions<AuthOriginOptions> origin, IOptions<DataProtectionTokenProviderOptions> tokens) : IManagerUserService
{
    public const int PageSize = 25;
    public static readonly (string Key, string Label)[] RoleFilters = [("all", "All"), (Roles.Customer, "Pet owners"), (Roles.Sitter, "Pet sitters"), (Roles.PlatformManager, "Managers")];

    public async Task<ManagedUserPage> ListAsync(string? search, string? role, string? status, int page, CancellationToken ct = default)
    {
        role = RoleFilters.Any(r => r.Key == role) ? role! : "all";
        status = status is "active" or "inactive" ? status : "all";
        page = Math.Max(1, page);
        var term = search?.Trim();
        var query = from u in db.Users
                    join ur in db.UserRoles on u.Id equals ur.UserId into userRoles
                    from ur in userRoles.DefaultIfEmpty()
                    join r in db.Roles on ur.RoleId equals r.Id into roles
                    from r in roles.DefaultIfEmpty()
                    select new { u.Id, u.FirstName, u.LastName, u.Email, u.IsActive, u.EmailConfirmed, u.ConcurrencyStamp, Role = r == null ? null : r.Name };
        if (!string.IsNullOrEmpty(term))
            query = query.Where(x => (x.FirstName + " " + x.LastName).Contains(term) || (x.Email != null && x.Email.Contains(term)));
        if (role != "all") query = query.Where(x => x.Role == role);
        if (status == "active") query = query.Where(x => x.IsActive);
        if (status == "inactive") query = query.Where(x => !x.IsActive);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.FirstName).ThenBy(x => x.LastName).ThenBy(x => x.Email)
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);
        return new(rows.Select(x => new ManagedUser(x.Id, (x.FirstName + " " + x.LastName).Trim(), x.Email, RoleLabel(x.Role), x.IsActive, x.EmailConfirmed, x.ConcurrencyStamp ?? "")).ToList(),
            total, page, PageSize, term, role, status);
    }

    public async Task<ManagerWriteResult> SetActiveAsync(Guid managerId, Guid userId, bool active, string? version, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return new(false, "User not found.", true);
        if (user.ConcurrencyStamp != version) return new(false, "This account changed. Refresh the list and try again.");
        if (user.IsActive == active) return new(true, active ? "This account is already active." : "This account is already switched off.");
        // A closed account was anonymised and locked for good; switching it on would not let anyone sign in.
        if (active && await db.AccountClosureRequests.AnyAsync(c => c.UserId == user.Id && c.Status == ClosureStatus.Approved, ct))
            return new(false, "This account was closed at the person's request and can't be switched back on. They can sign up again with a new account.");
        if (!active)
        {
            if (user.Id == managerId) return new(false, "You can't switch off your own account.");
            if (await users.IsInRoleAsync(user, Roles.PlatformManager) && (await users.GetUsersInRoleAsync(Roles.PlatformManager)).Count(m => m.IsActive) <= 1)
                return new(false, "This is the only active manager. Add another manager first.");
            var busy = await db.PetBookings.AnyAsync(b => (b.CustomerId == user.Id || b.SitterProfile.UserId == user.Id) &&
                (b.Status == BookingStatus.Pending || b.Status == BookingStatus.AwaitingPayment || b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.InProgress), ct);
            if (busy) return new(false, "This account has active bookings. They must be completed or cancelled before it can be switched off.");
        }
        user.IsActive = active;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded) return new(false, "This account changed. Refresh the list and try again.");
        // A new security stamp signs the person out everywhere at their next request.
        if (!active) await users.UpdateSecurityStampAsync(user);
        return new(true, active ? user.FirstName + "'s account is active again." : user.FirstName + "'s account is switched off and they were signed out.");
    }

    public async Task<ManagerWriteResult> AddManagerAsync(AddManagerForm form, CancellationToken ct = default)
    {
        var address = form.Email.Trim();
        if (await users.FindByEmailAsync(address) is not null) return new(false, "An account with this email already exists. Use a different email for the manager.");
        var manager = new ApplicationUser { Id = Guid.NewGuid(), UserName = address, Email = address, EmailConfirmed = true,
            FirstName = form.FirstName.Trim(), LastName = form.LastName.Trim() };
        var created = await users.CreateAsync(manager);   // no password: they choose one from the emailed link
        if (!created.Succeeded) return new(false, string.Join(" ", created.Errors.Select(e => e.Description)));
        var assigned = await users.AddToRoleAsync(manager, Roles.PlatformManager);
        if (!assigned.Succeeded) { await users.DeleteAsync(manager); return new(false, string.Join(" ", assigned.Errors.Select(e => e.Description))); }
        var token = await users.GeneratePasswordResetTokenAsync(manager);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = QueryHelpers.AddQueryString(origin.Value.PublicOrigin.TrimEnd('/') + "/Identity/Account/ResetPassword", "code", code);
        var hours = tokens.Value.TokenLifespan.TotalHours.ToString("0.##");
        try
        {
            await email.SendEmailAsync(address, "You're a CareConnect platform manager",
                "<p>Hi " + HtmlEncoder.Default.Encode(manager.FirstName) + ",</p><p>You were added as a platform manager. " +
                "<a href='" + HtmlEncoder.Default.Encode(link) + "'>Choose your password</a> to sign in.</p>" +
                "<p>This link expires in " + hours + " hours. After that, use \"Forgot password\" on the sign-in page.</p>");
        }
        catch (Exception) { return new(true, manager.FirstName + " was added, but the email didn't send. They can use \"Forgot password\" on the sign-in page."); }
        return new(true, manager.FirstName + " was added as a manager and emailed a link to choose a password.");
    }

    private static string RoleLabel(string? role) => role switch
    {
        Roles.Customer => "Pet owner", Roles.Sitter => "Pet sitter", Roles.PlatformManager => "Manager", _ => "No role"
    };
}
