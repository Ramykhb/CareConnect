using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Auth;
using BabySitting.Services.Dashboard;
using Xunit;

namespace BabySitting.Tests;

public sealed class ParentSettingsTests
{
    [Theory]
    [InlineData("/ParentSettings")]
    [InlineData("/ParentSettings/Photo")]
    public async Task Settings_and_photo_require_sign_in(string path)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Identity/Account/Login", response.Headers.Location!.ToString());
    }

    [SqlServerFact]
    public async Task Owner_settings_save_profile_photo_alerts_and_secure_sessions()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var current = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var second = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var other = factory.CreateClient(new() { AllowAutoRedirect = false });
        var key = Guid.NewGuid().ToString("N"); var email = $"settings-{key}@example.test"; var otherEmail = $"settings-other-{key}@example.test";
        var ownerId = Guid.Empty; var otherId = Guid.Empty;
        const string password = "Original!Password123";
        const string changedPassword = "Changed!Password456";
        try
        {
            using (var setup = factory.Services.CreateScope())
            {
                var registration = setup.ServiceProvider.GetRequiredService<RegistrationService>();
                var users = setup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                foreach (var address in new[] { email, otherEmail })
                {
                    var (result, user) = await registration.RegisterAsync(address, password, "Owner", "Settings", "Customer");
                    Assert.True(result.Succeeded); user!.EmailConfirmed = true;
                    Assert.True((await users.UpdateAsync(user)).Succeeded);
                    if (address == email) ownerId = user.Id; else otherId = user.Id;
                }
                var db = setup.ServiceProvider.GetRequiredService<BabySittingDbContext>();
                db.Notifications.AddRange(new Notification { UserId = ownerId, Title = "Hidden request update", Body = "Request", Category = NotificationCategory.Request },
                    new Notification { UserId = ownerId, Title = "Visible booking update", Body = "Booking", Category = NotificationCategory.Booking },
                    new Notification { UserId = ownerId, Title = "General update", Body = "General", Category = NotificationCategory.General });
                await db.SaveChangesAsync();
            }
            await Login(current, email, password); await Login(second, email, password); await Login(other, otherEmail, password);
            var html = await current.GetStringAsync("/ParentSettings");
            Assert.Contains("Account settings", html);
            Assert.Contains("Two-factor authentication", html);
            Assert.Contains("This browser", html);
            Assert.DoesNotContain("Chrome · Windows", html);
            var profile = new Dictionary<string, string>
            {
                ["Profile.FirstName"] = "Sarah", ["Profile.LastName"] = "Owner", ["Profile.Phone"] = "+961 71 234 567", ["Profile.City"] = "Beirut",
                ["Profile.HomeAddress"] = "42 Owner Street, Achrafieh", ["Profile.Language"] = "fr", ["Profile.EmergencyContact"] = "Rami · +961 3 445 120",
                ["Profile.Version"] = Hidden(html, "Profile.Version"), ["Profile.IsActive"] = "false", ["Profile.EmailConfirmed"] = "false"
            };
            Assert.Equal(HttpStatusCode.BadRequest, (await current.PostAsync("/ParentSettings/Profile", new FormUrlEncodedContent(profile))).StatusCode);
            var invalid = new Dictionary<string,string>(profile) { ["Profile.Language"] = "invalid" };
            Assert.Equal(HttpStatusCode.OK, (await Post(current, "/ParentSettings/Profile", invalid)).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await Post(current, "/ParentSettings/Profile", profile)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/ParentSettings")).StatusCode);
            var stale = new Dictionary<string,string>(profile) { ["Profile.FirstName"] = "Stale overwrite" };
            var conflict = await Post(current, "/ParentSettings/Profile", stale);
            Assert.Equal(HttpStatusCode.OK, conflict.StatusCode);
            Assert.Contains("changed in another tab", await conflict.Content.ReadAsStringAsync());
            using (var check = factory.Services.CreateScope())
            {
                var saved = await check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(ownerId.ToString());
                Assert.Equal("Sarah", saved!.FirstName); Assert.Equal("fr", saved.PreferredLanguage); Assert.Equal(profile["Profile.HomeAddress"], saved.HomeAddress);
                Assert.Equal(profile["Profile.EmergencyContact"], saved.EmergencyContact); Assert.True(saved.IsActive); Assert.True(saved.EmailConfirmed);
            }
            var wizard = await current.GetStringAsync("/CareRequests/Create?fresh=true");
            Assert.Contains("42 Owner Street, Achrafieh", wizard);
            Assert.Equal(HttpStatusCode.Redirect, (await Upload(current, Encoding.UTF8.GetBytes("<svg><script>unsafe</script></svg>"), "image.svg")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await current.GetAsync("/ParentSettings/Photo")).StatusCode);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2eQAAAABJRU5ErkJggg==");
            Assert.Equal(HttpStatusCode.Redirect, (await Upload(current, png, "photo.png")).StatusCode);
            var photo = await current.GetAsync("/ParentSettings/Photo"); photo.EnsureSuccessStatusCode();
            Assert.Equal("image/png", photo.Content.Headers.ContentType!.MediaType);
            Assert.Equal(png, await photo.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/ParentSettings/Photo?userId=" + ownerId)).StatusCode);
            Assert.Contains("Your profile photo", await current.GetStringAsync("/ParentSettings"));
            await Post(current, "/ParentSettings/Alerts", new() { ["Alerts.Bookings"] = "true" });
            using (var check = factory.Services.CreateScope())
            {
                var users = check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var account = (await users.FindByIdAsync(ownerId.ToString()))!;
                var dashboard = await check.ServiceProvider.GetRequiredService<IDashboardService>().LoadAsync(account, false);
                Assert.DoesNotContain(dashboard.Notifications, n => n.Title == "Hidden request update");
                Assert.Contains(dashboard.Notifications, n => n.Title == "Visible booking update"); Assert.Equal(2, dashboard.Unread);
            }
            await Post(current, "/ParentSettings/Sessions", new() { ["currentPassword"] = "wrong" });
            Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/ParentSettings")).StatusCode);
            await Post(current, "/ParentSettings/Sessions", new() { ["currentPassword"] = password });
            Assert.Equal(HttpStatusCode.Redirect, (await second.GetAsync("/ParentSettings")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/ParentSettings")).StatusCode);
            var passwordFields = new Dictionary<string,string> { ["PasswordChange.CurrentPassword"] = "Wrong!Password123", ["PasswordChange.NewPassword"] = changedPassword, ["PasswordChange.ConfirmPassword"] = changedPassword };
            var wrong = await Post(current, "/ParentSettings/Password", passwordFields);
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
            Assert.DoesNotContain("value=\"Wrong!Password123\"", await wrong.Content.ReadAsStringAsync());
            passwordFields["PasswordChange.CurrentPassword"] = password;
            Assert.Equal(HttpStatusCode.Redirect, (await Post(current, "/ParentSettings/Password", passwordFields)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/ParentSettings")).StatusCode);
            await Login(second, email, changedPassword);
            using var verify = factory.Services.CreateScope();
            Assert.True(await verify.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CheckPasswordAsync((await verify.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(ownerId.ToString()))!, changedPassword));
        }
        finally
        {
            using var cleanup = factory.Services.CreateScope(); var db = cleanup.ServiceProvider.GetRequiredService<BabySittingDbContext>();
            await db.Notifications.Where(n => n.UserId == ownerId || n.UserId == otherId).ExecuteDeleteAsync();
            await db.AlertPreferences.Where(p => p.UserId == ownerId || p.UserId == otherId).ExecuteDeleteAsync();
            var users = cleanup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var address in new[] { email, otherEmail }) { var user = await users.FindByEmailAsync(address); if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded); }
            Assert.False(await db.AccountPhotos.AnyAsync(p => p.UserId == ownerId));
        }
    }
    private static string Hidden(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, $"<input(?=[^>]*name=\"{Regex.Escape(name)}\")(?=[^>]*value=\"([^\"]*)\")[^>]*>").Groups[1].Value);
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string,string> fields)
    {
        fields["__RequestVerificationToken"] = Hidden(await client.GetStringAsync("/Identity/Account/Login"), "__RequestVerificationToken");
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
    private static async Task Login(HttpClient client, string email, string password) => Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = password })).StatusCode);
    private static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] bytes, string fileName)
    {
        using var data = new MultipartFormDataContent();
        data.Add(new StringContent(Hidden(await client.GetStringAsync("/ParentSettings"), "__RequestVerificationToken")), "__RequestVerificationToken");
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); data.Add(file, "photo", fileName);
        return await client.PostAsync("/ParentSettings/UploadPhoto", data);
    }
}
