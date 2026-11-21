using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using Xunit;

namespace PetSitting.Tests;

// A manager only sees links they can open: Overview, Settings, the manager tabs and Log out.
public sealed class ManagerNavigationTests
{
    private const string Password = "Test!Password123";

    [SqlServerFact]
    public async Task Every_link_a_manager_sees_opens_and_owner_pages_and_privacy_are_hidden()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        var email = $"nav-manager-{Guid.NewGuid():N}@example.test";
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var manager = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Nia", LastName = "NavTest", UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(manager, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(manager, Roles.PlatformManager)).Succeeded);
        }
        try
        {
            using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
            var login = await client.GetStringAsync("/Identity/Account/Login");
            var signedIn = await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = Hidden(login, "__RequestVerificationToken"), ["Input.Email"] = email, ["Input.Password"] = Password
            }));
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);

            foreach (var page in new[] { "/ManagerReports", "/Identity/Account/Manage", "/Identity/Account/Manage/ChangePassword", "/SitterVerifications", "/Payouts" })
            {
                var html = await client.GetStringAsync(page);
                var topBar = Regex.Match(html, "<nav aria-label=\"Main navigation\">(.*?)</nav>", RegexOptions.Singleline).Groups[1].Value;
                Assert.Contains(">Overview<", topBar);
                Assert.Contains(">Settings<", topBar);
                Assert.DoesNotContain("Privacy", topBar);
                Assert.DoesNotContain(">Home<", topBar);
                Assert.DoesNotContain("portal-sidebar", html);                       // no owner/sitter portal menu
                foreach (var href in Regex.Matches(html, "href=\"(/[^\"#]*)\"").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).Distinct())
                {
                    if (href.StartsWith("/css/") || href.StartsWith("/js/") || href.StartsWith("/lib/") || href.Contains("Export") || href.Contains("Document")) continue;
                    var response = await client.GetAsync(href);
                    Assert.False(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.InternalServerError, $"{page} links to {href}: {response.StatusCode}");
                    Assert.DoesNotContain("AccessDenied", response.Headers.Location?.ToString() ?? "");
                }
            }
            // On the settings page the Settings tab is the highlighted one.
            Assert.Matches("class=\"nav-link active\"[^>]*>Settings<", await client.GetStringAsync("/Identity/Account/Manage"));
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (await users.FindByEmailAsync(email) is { } user)
                for (var attempt = 1; ; attempt++)
                {
                    await scope.ServiceProvider.GetRequiredService<PetSittingDbContext>().Notifications.Where(n => n.UserId == user.Id).ExecuteDeleteAsync();
                    try { Assert.True((await users.DeleteAsync(user)).Succeeded); break; }
                    catch (DbUpdateException) when (attempt < 5) { }
                }
        }
    }

    [Fact]
    public async Task Visitors_still_see_home_and_privacy()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        var html = await factory.CreateClient().GetStringAsync("/Home/Privacy");
        var topBar = Regex.Match(html, "<nav aria-label=\"Main navigation\">(.*?)</nav>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("Privacy", topBar);
        Assert.Contains(">Home<", topBar);
    }

    private static string Hidden(string html, string name) =>
        WebUtility.HtmlDecode(Regex.Match(html, $"<input(?=[^>]*name=\"{name}\")(?=[^>]*value=\"([^\"]*)\")[^>]*>").Groups[1].Value);
}
