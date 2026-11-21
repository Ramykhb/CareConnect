using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Auth;
using Xunit;

namespace BabySitting.Tests;

// Managers always start on their home page after login, so they never land on "Access denied".
public sealed class LoginDestinationTests
{
    private const string Password = "Test!Password123";

    [Theory]
    [InlineData(true, "/Sitter/Settings", "/Dashboard")]
    [InlineData(true, "/ManagerReports", "/Dashboard")]
    [InlineData(true, "/Dashboard", "/Dashboard")]
    [InlineData(false, "/ManagerReports", "/Dashboard")]
    [InlineData(false, "/payouts?status=all", "/Dashboard")]
    [InlineData(false, "/SitterVerifications", "/Dashboard")]
    [InlineData(false, "/Sitter/Settings", "/Sitter/Settings")]
    [InlineData(false, "/Notifications", "/Notifications")]
    public void Destination_after_login(bool manager, string returnUrl, string expected) =>
        Assert.Equal(expected, LoginDestination.For(manager, returnUrl));

    [SqlServerFact]
    public async Task Managers_land_on_their_home_page_and_others_never_land_on_manager_pages()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        var key = Guid.NewGuid().ToString("N")[..12];
        var managerEmail = $"login-manager-{key}@example.test";
        var sitterEmail = $"login-sitter-{key}@example.test";
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var manager = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Mia", LastName = "LoginTest", UserName = managerEmail, Email = managerEmail, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(manager, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(manager, Roles.PlatformManager)).Succeeded);
            var (result, sitter) = await scope.ServiceProvider.GetRequiredService<RegistrationService>().RegisterAsync(sitterEmail, Password, "Sid", "LoginTest", "Sitter");
            Assert.True(result.Succeeded);
            sitter!.EmailConfirmed = true;
            Assert.True((await users.UpdateAsync(sitter)).Succeeded);
        }
        try
        {
            // The manager opened the login page from a sitter page: they still start on their home page.
            using var manager = factory.CreateClient(new() { AllowAutoRedirect = false });
            Assert.Equal("/Dashboard", await Login(manager, managerEmail, "/Sitter/Settings"));
            Assert.Equal("/Dashboard/Manager", (await manager.GetAsync("/Dashboard")).Headers.Location!.OriginalString);
            Assert.Equal("/ManagerReports", (await manager.GetAsync("/Dashboard/Manager")).Headers.Location!.OriginalString);
            Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/ManagerReports")).StatusCode);

            // A sitter is never sent to a manager page, but keeps their own deep link.
            using var sitter = factory.CreateClient(new() { AllowAutoRedirect = false });
            Assert.Equal("/Dashboard", await Login(sitter, sitterEmail, "/ManagerReports"));
            using var sitter2 = factory.CreateClient(new() { AllowAutoRedirect = false });
            Assert.Equal("/Sitter/Settings", await Login(sitter2, sitterEmail, "/Sitter/Settings"));

            // Opening another role's page shows the friendly page with a way home.
            var denied = await sitter.GetAsync("/ManagerReports");
            Assert.Contains("AccessDenied", denied.Headers.Location!.ToString());
            var page = await sitter.GetStringAsync(denied.Headers.Location!.ToString());
            Assert.Contains("for your account", page);
            Assert.Contains("Go to my home page", page);
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var email in new[] { managerEmail, sitterEmail })
                if (await users.FindByEmailAsync(email) is { } user)
                {
                    // Other tests may have just notified this manager (for example about a sitter to verify).
                    for (var attempt = 1; ; attempt++)
                    {
                        await scope.ServiceProvider.GetRequiredService<BabySittingDbContext>().Notifications.Where(n => n.UserId == user.Id).ExecuteDeleteAsync();
                        try { Assert.True((await users.DeleteAsync(user)).Succeeded); break; }
                        catch (DbUpdateException) when (attempt < 5) { }
                    }
                }
        }
    }

    private static string Hidden(string html, string name) =>
        WebUtility.HtmlDecode(Regex.Match(html, $"<input(?=[^>]*name=\"{name}\")(?=[^>]*value=\"([^\"]*)\")[^>]*>").Groups[1].Value);

    // Signs in from the login page opened with ?ReturnUrl=... and returns where login sends the person.
    private static async Task<string> Login(HttpClient client, string email, string returnUrl)
    {
        var url = "/Identity/Account/Login?returnUrl=" + Uri.EscapeDataString(returnUrl);
        var form = await client.GetStringAsync(url);
        var response = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Hidden(form, "__RequestVerificationToken"), ["Input.Email"] = email, ["Input.Password"] = Password
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }
}
