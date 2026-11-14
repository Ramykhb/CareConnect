using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Bookings;
using BabySitting.Services.Portal;
using BabySitting.ViewModels;

namespace BabySitting.Controllers;

[Authorize(Roles = Roles.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ParentSettingsController(BabySittingDbContext db, UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn, ISitterAccountService accounts) : Controller
{
    private Guid OwnerId => Guid.Parse(users.GetUserId(User)!);
    private async Task<ParentSettingsViewModel?> Load()
    {
        var account = await users.GetUserAsync(User); if (account is null) return null;
        var alerts = await db.AlertPreferences.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == account.Id) ?? new();
        var auth = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        return new() { Account = account, HasPassword = await users.HasPasswordAsync(account), TwoFactorEnabled = await users.GetTwoFactorEnabledAsync(account), SignedInAt = auth.Properties?.IssuedUtc,
            Profile = new() { FirstName = account.FirstName, LastName = account.LastName, City = account.City, Phone = account.PhoneNumber,
                HomeAddress = account.HomeAddress, Language = account.PreferredLanguage, EmergencyContact = account.EmergencyContact, Version = account.ConcurrencyStamp ?? "" },
            Alerts = new() { Version = alerts.RowVersion.Length == 0 ? "" : Convert.ToBase64String(alerts.RowVersion), Requests = alerts.Requests, Bookings = alerts.Bookings, Payments = alerts.Payments, Reviews = alerts.Reviews, RequestSound = alerts.RequestSound } };
    }
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var model = await Load(); return model is null ? Challenge() : View(model);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(ParentProfileForm profile)
    {
        var model = await Load(); if (model is null) return Challenge();
        if (model.Account.ConcurrencyStamp != profile.Version) ModelState.AddModelError("", "Your profile changed in another tab. Check your entries before saving again.");
        if (!ModelState.IsValid)
        {
            profile.Version = model.Account.ConcurrencyStamp ?? ""; ModelState.Remove("Profile.Version"); model.Profile = profile; return View("Index", model);
        }
        var account = model.Account;
        account.FirstName = profile.FirstName.Trim(); account.LastName = profile.LastName.Trim(); account.City = Clean(profile.City);
        var phone = Clean(profile.Phone); if (account.PhoneNumber != phone) account.PhoneNumberConfirmed = false;
        account.PhoneNumber = phone; account.HomeAddress = Clean(profile.HomeAddress); account.EmergencyContact = Clean(profile.EmergencyContact); account.PreferredLanguage = profile.Language;
        var result = await users.UpdateAsync(account);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
            model.Profile = profile; return View("Index", model);
        }
        await signIn.RefreshSignInAsync(account); TempData["Success"] = "Your profile has been updated."; return RedirectToAction(nameof(Index));
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Password(ParentPasswordForm passwordChange)
    {
        var model = await Load(); if (model is null) return Challenge();
        if (ModelState.IsValid)
        {
            var result = await users.ChangePasswordAsync(model.Account, passwordChange.CurrentPassword, passwordChange.NewPassword);
            if (result.Succeeded)
            {
                await signIn.RefreshSignInAsync(model.Account); TempData["Success"] = "Your password was changed. Other sessions will need to sign in again.";
                return RedirectToAction(nameof(Index), null, null, "security-settings");
            }
            foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
        }
        // Password fields are always re-entered; never put submitted passwords into rendered HTML.
        ViewData["SettingsSection"] = "security-settings";
        model.PasswordChange = new(); return View("Index", model);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Sessions(string? currentPassword)
    {
        var account = await users.GetUserAsync(User); if (account is null) return Challenge();
        if (string.IsNullOrEmpty(currentPassword) || !await users.CheckPasswordAsync(account, currentPassword))
        { TempData["Error"] = "Enter your current password to sign out other sessions."; return RedirectToAction(nameof(Index), null, null, "security-settings"); }
        var result = await users.UpdateSecurityStampAsync(account);
        if (result.Succeeded) { await signIn.RefreshSignInAsync(account); TempData["Success"] = "Other sessions have been signed out. This session stays active."; }
        else TempData["Error"] = "Couldn’t sign out other sessions. Try again.";
        return RedirectToAction(nameof(Index), null, null, "security-settings");
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Alerts(AlertPreferencesForm alerts)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "Check your notification preferences and try again."; return RedirectToAction(nameof(Index), null, null, "notification-settings"); }
        // Sound is a sitter-inbox preference; parent settings leave that independent value intact.
        alerts.RequestSound = await db.AlertPreferences.Where(p => p.UserId == OwnerId).Select(p => p.RequestSound).SingleOrDefaultAsync();
        var result = await accounts.SaveAlertsAsync(OwnerId, alerts);
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Index), null, null, "notification-settings");
    }
    [HttpGet]
    public async Task<IActionResult> Photo()
    {
        var photo = await db.AccountPhotos.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == OwnerId);
        if (photo is null) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff"; Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return File(photo.Bytes, photo.ContentType);
    }
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(2300000)]
    public async Task<IActionResult> UploadPhoto(IFormFile? photo)
    {
        if (photo is null || photo.Length is <= 0 or > 2097152) return PhotoError("Choose a JPEG, PNG or WebP photo up to 2 MB.");
        await using var source = photo.OpenReadStream(); using var output = new MemoryStream();
        var buffer = new byte[81920]; int count;
        while ((count = await source.ReadAsync(buffer)) > 0)
        { if (output.Length + count > 2097152) return PhotoError("Choose a photo up to 2 MB."); await output.WriteAsync(buffer.AsMemory(0, count)); }
        var bytes = output.ToArray(); var type = SitterBookingService.ImageType(bytes);
        if (type is null) return PhotoError("Choose a valid JPEG, PNG or WebP image.");
        try
        {
            var saved = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync();
                var account = await users.FindByIdAsync(OwnerId.ToString()); if (account is null) return false;
                var stored = await db.AccountPhotos.SingleOrDefaultAsync(p => p.UserId == account.Id);
                if (stored is null) { stored = new() { UserId = account.Id }; db.AccountPhotos.Add(stored); }
                stored.Bytes = bytes; stored.ContentType = type;
                account.ProfilePhotoVersion = Guid.NewGuid();
                var result = await users.UpdateAsync(account); if (!result.Succeeded) return false;
                await db.SaveChangesAsync(); await tx.CommitAsync(); return true;
            });
            if (!saved) return PhotoError("Your account changed. Reload settings and try again.");
        }
        catch (DbUpdateException) { return PhotoError("Your photo changed. Reload settings and try again."); }
        TempData["Success"] = "Your profile photo was updated."; return RedirectToAction(nameof(Index));
    }
    private IActionResult PhotoError(string message) { TempData["Error"] = message; return RedirectToAction(nameof(Index)); }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
