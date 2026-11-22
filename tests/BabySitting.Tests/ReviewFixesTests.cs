using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Repositories;
using BabySitting.Services.Accounts;
using BabySitting.ViewModels;
using Xunit;

namespace BabySitting.Tests;

// Fixes from the full code review.
public sealed class ReviewFixesTests
{
    private const string Password = "Test!Password123";
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));

    [Fact]
    public void An_edited_request_needs_a_budget_of_at_least_1()
    {
        var form = new BabySitting.ViewModels.CareRequestFormViewModel { Budget = 0 };
        var errors = new List<ValidationResult>();
        Validator.TryValidateProperty(form.Budget, new ValidationContext(form) { MemberName = nameof(form.Budget) }, errors);
        Assert.NotEmpty(errors);
        errors.Clear();
        Assert.True(Validator.TryValidateProperty(1m, new ValidationContext(form) { MemberName = nameof(form.Budget) }, errors));
    }

    [SqlServerFact]
    public async Task Editing_a_request_sends_an_already_sent_offer_back_to_the_sitter()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        ApplicationUser NewUser(string name) => new() { Id = Guid.NewGuid(), FirstName = name, LastName = "ReviewFix", UserName = "reviewfix-" + Guid.NewGuid().ToString("N") };
        var owner = NewUser("Owner"); var sitter = NewUser("Sitter");
        var profile = new BabysitterProfile { User = sitter, Bio = "Test", City = "Beirut", HourlyRate = 10 };
        var start = DateTimeOffset.UtcNow.AddDays(5);
        var request = new ChildcareRequest { Customer = owner, Title = "Evening care", Description = "Test", City = "Beirut",
            StartsAtUtc = start, EndsAtUtc = start.AddHours(4), Budget = 100, Status = RequestStatus.Open, CustomerChoosesSitter = true,
            NumberOfChildren = 1, YoungestChildAgeYears = 3, OldestChildAgeYears = 3 };
        var offer = new BabysittingOffer { Request = request, SitterProfile = profile, Amount = 100, Status = OfferStatus.Pending, SubmittedAtUtc = DateTimeOffset.UtcNow };
        db.AddRange(owner, sitter, profile, request, offer);
        await db.SaveChangesAsync();
        try
        {
            db.ChangeTracker.Clear();
            var version = (await db.ChildcareRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).RowVersion;
            Assert.Equal(CareRequestWriteResult.Saved, await scope.ServiceProvider.GetRequiredService<ICareRequestRepository>()
                .UpdateAsync(owner.Id, request.Id, version, r => { r.Budget = 40; r.Description = "Changed"; }, DateTimeOffset.UtcNow));
            db.ChangeTracker.Clear();
            var saved = await db.BabysittingOffers.AsNoTracking().SingleAsync(o => o.Id == offer.Id);
            Assert.Null(saved.SubmittedAtUtc);                       // no longer counts as sent: the owner can't choose it on the new terms
            Assert.Equal(OfferStatus.Pending, saved.Status);
            Assert.Equal(40, saved.Amount);
            var alert = await db.Notifications.AsNoTracking().SingleAsync(n => n.UserId == sitter.Id && n.Title == "A request you made an offer on changed");
            Assert.Equal("/SitterRequests?id=" + request.Id, alert.RelativeUrl);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Notifications.Where(n => n.UserId == sitter.Id || n.UserId == owner.Id).ExecuteDeleteAsync();
            await db.BabysittingOffers.Where(o => o.RequestId == request.Id).ExecuteDeleteAsync();
            await db.ChildcareRequests.Where(r => r.Id == request.Id).ExecuteDeleteAsync();
            await db.BabysitterProfiles.Where(p => p.Id == profile.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == sitter.Id).ExecuteDeleteAsync();
        }
    }

    [SqlServerFact]
    public async Task A_closed_account_loses_its_Google_login_and_cannot_be_switched_back_on()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var key = Guid.NewGuid().ToString("N");
        var person = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Gia", LastName = "ReviewFix", UserName = $"closed-google-{key}@example.test", Email = $"closed-google-{key}@example.test", EmailConfirmed = true };
        var manager = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Max", LastName = "ReviewFix", UserName = $"closed-manager-{key}@example.test", Email = $"closed-manager-{key}@example.test", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(person)).Succeeded);
        Assert.True((await users.AddLoginAsync(person, new UserLoginInfo("Google", "google-" + key, "Google"))).Succeeded);
        Assert.True((await users.CreateAsync(manager)).Succeeded);
        Assert.True((await users.AddToRoleAsync(manager, Roles.PlatformManager)).Succeeded);
        try
        {
            var closures = scope.ServiceProvider.GetRequiredService<IAccountClosureService>();
            Assert.True((await closures.RequestAsync(person.Id, "Leaving")).Succeeded);
            var request = (await closures.PendingForUserAsync(person.Id))!;
            var version = Convert.ToBase64String(db.AccountClosureRequests.AsNoTracking().Single(r => r.Id == request.Id).RowVersion);
            var approved = await closures.ApproveAsync(manager.Id, request.Id, null, version);
            Assert.True(approved.Succeeded, approved.Message);

            // The Google login is gone, so the same Google account can sign up again.
            Assert.Null(await users.FindByLoginAsync("Google", "google-" + key));

            // Switching the closed account back on is refused with a clear reason.
            var closed = (await users.FindByIdAsync(person.Id.ToString()))!;
            var result = await scope.ServiceProvider.GetRequiredService<IManagerUserService>().SetActiveAsync(manager.Id, person.Id, true, closed.ConcurrencyStamp);
            Assert.False(result.Succeeded);
            Assert.Contains("closed", result.Message);
            Assert.False((await db.Users.AsNoTracking().SingleAsync(u => u.Id == person.Id)).IsActive);
        }
        finally
        {
            db.ChangeTracker.Clear();
            var ids = new[] { person.Id, manager.Id };
            await db.AccountClosureRequests.Where(r => ids.Contains(r.UserId)).ExecuteDeleteAsync();
            await db.UserLogins.Where(l => ids.Contains(l.UserId)).ExecuteDeleteAsync();
            await db.UserRoles.Where(r => ids.Contains(r.UserId)).ExecuteDeleteAsync();
            for (var attempt = 1; ; attempt++)
            {
                await db.Notifications.Where(n => ids.Contains(n.UserId)).ExecuteDeleteAsync();
                try { await db.Users.Where(u => ids.Contains(u.Id)).ExecuteDeleteAsync(); break; }
                catch (Microsoft.Data.SqlClient.SqlException) when (attempt < 5) { }
            }
        }
    }

    [SqlServerFact]
    public async Task A_manager_cannot_delete_their_own_account()
    {
        await using var factory = Factory();
        var email = $"delete-manager-{Guid.NewGuid():N}@example.test";
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var manager = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Dee", LastName = "ReviewFix", UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(manager, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(manager, Roles.PlatformManager)).Succeeded);
        }
        try
        {
            using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
            var login = await client.GetStringAsync("/Identity/Account/Login");
            Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = Hidden(login, "__RequestVerificationToken"), ["Input.Email"] = email, ["Input.Password"] = Password }))).StatusCode);

            var page = await client.GetStringAsync("/Identity/Account/Manage/DeletePersonalData");
            Assert.Contains("Manager accounts can", page);
            Assert.DoesNotContain("Delete my account permanently", page);

            // Even a hand-made post is refused.
            var posted = await client.PostAsync("/Identity/Account/Manage/DeletePersonalData", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = Hidden(page, "__RequestVerificationToken"), ["Input.Password"] = Password }));
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
            using var scope = factory.Services.CreateScope();
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email));
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (await users.FindByEmailAsync(email) is { } user)
                for (var attempt = 1; ; attempt++)
                {
                    await scope.ServiceProvider.GetRequiredService<BabySittingDbContext>().Notifications.Where(n => n.UserId == user.Id).ExecuteDeleteAsync();
                    try { Assert.True((await users.DeleteAsync(user)).Succeeded); break; }
                    catch (DbUpdateException) when (attempt < 5) { }
                }
        }
    }

    [Fact]
    public void The_edit_form_requires_the_home_address()
    {
        var form = new CareRequestFormViewModel { NumberOfChildren = 1, YoungestChildAgeYears = 3, OldestChildAgeYears = 3, ServiceAddress = "  " };
        Assert.Contains(form.Validate(new ValidationContext(form)), r => r.MemberNames.Contains(nameof(form.ServiceAddress)));
        form.ServiceAddress = "Street 1, Beirut";
        Assert.DoesNotContain(form.Validate(new ValidationContext(form)), r => r.MemberNames.Contains(nameof(form.ServiceAddress)));
    }

    [SqlServerFact]
    public async Task Editing_a_request_updates_the_saved_wizard_copy_used_by_book_again()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var parent = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Pam", LastName = "ReviewFix", UserName = "reviewfix-" + Guid.NewGuid().ToString("N") };
        var start = DateTimeOffset.UtcNow.AddDays(5);
        var request = new ChildcareRequest { Customer = parent, Title = "Evening care", Description = "Old notes", City = "Beirut", ServiceAddress = "Old street",
            StartsAtUtc = start, EndsAtUtc = start.AddHours(4), Budget = 100, Status = RequestStatus.Open, NumberOfChildren = 1, YoungestChildAgeYears = 3, OldestChildAgeYears = 3 };
        var draft = new ParentRequestDraft { Customer = parent, SubmittedRequest = request, Step = 6,
            DataJson = System.Text.Json.JsonSerializer.Serialize(new ParentRequestForm { City = "Beirut", StreetAddress = "Old street", Instructions = "Old notes", Budget = 100 }) };
        db.AddRange(parent, request, draft);
        await db.SaveChangesAsync();
        try
        {
            db.ChangeTracker.Clear();
            var version = (await db.ChildcareRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).RowVersion;
            Assert.Equal(CareRequestWriteResult.Saved, await scope.ServiceProvider.GetRequiredService<ICareRequestRepository>().UpdateAsync(parent.Id, request.Id, version, r =>
            { r.City = "Jounieh"; r.ServiceAddress = "New street"; r.Description = "New notes"; r.Budget = 60; r.NumberOfChildren = 2; r.OldestChildAgeYears = 6; }, DateTimeOffset.UtcNow));
            db.ChangeTracker.Clear();
            var saved = System.Text.Json.JsonSerializer.Deserialize<ParentRequestForm>((await db.ParentRequestDrafts.AsNoTracking().SingleAsync(d => d.Id == draft.Id)).DataJson)!;
            Assert.Equal(("Jounieh", "New street", "New notes", 60m, 2, 6), (saved.City, saved.StreetAddress, saved.Instructions, saved.Budget!.Value, saved.NumberOfChildren, saved.OldestChildAgeYears));
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.ParentRequestDrafts.Where(d => d.CustomerId == parent.Id).ExecuteDeleteAsync();
            await db.ChildcareRequests.Where(r => r.Id == request.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == parent.Id).ExecuteDeleteAsync();
        }
    }

    [Theory]
    [InlineData("Beruit", "Beirut")]      // swapped letters (the request that never reached any sitter)
    [InlineData("Bruit", "Beirut")]
    [InlineData("beirutt", "Beirut")]
    [InlineData("Beirut", null)]          // exact match (any capitals): nothing to ask
    [InlineData("BEIRUT", null)]
    [InlineData("Jounieh", null)]         // a different city: allowed, no suggestion
    [InlineData("Be", null)]              // too short to guess
    public void Likely_city_typos_get_a_suggestion(string typed, string? expected) =>
        Assert.Equal(expected, BabySitting.Services.Requests.CityNames.Suggest(typed, ["beirut", "Zahle"]));

    private static string Hidden(string html, string name) =>
        WebUtility.HtmlDecode(Regex.Match(html, $"<input(?=[^>]*name=\"{name}\")(?=[^>]*value=\"([^\"]*)\")[^>]*>").Groups[1].Value);
}
