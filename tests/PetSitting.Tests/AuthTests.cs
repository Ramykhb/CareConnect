using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PetSitting.Models;
using PetSitting.Services.Auth;
using Xunit;

namespace PetSitting.Tests;

public sealed class AuthTests
{
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset current = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }

    private sealed class Mailbox : IEmailSender
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public Task SendEmailAsync(string email, string subject, string htmlMessage)
        { Messages.Enqueue(htmlMessage); return Task.CompletedTask; }
        public string LastLink() => WebUtility.HtmlDecode(Regex.Match(Messages.Last(), "href=['\"]([^'\"]+)").Groups[1].Value);
    }

    [Fact]
    public void Reset_cooldown_is_atomic_case_insensitive_and_expires()
    {
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var clock = new TestClock();
        var cooldown = new PasswordResetCooldown(cache, clock);
        var reservations = new ConcurrentBag<bool>();
        Parallel.For(0, 12, i => reservations.Add(cooldown.Reserve(i % 2 == 0 ? "person@example.test" : " PERSON@EXAMPLE.TEST ").Allowed));
        Assert.Single(reservations, allowed => allowed);
        Assert.True(cooldown.Reserve("person@example.test", "email-confirmation").Allowed);
        Assert.False(cooldown.Reserve("PERSON@example.test", "email-confirmation").Allowed);
        clock.Advance(TimeSpan.FromSeconds(44));
        Assert.False(cooldown.Reserve("person@example.test").Allowed);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(cooldown.Reserve("person@example.test").Allowed);
    }

    [Theory]
    [InlineData("/Identity/Account/Login")]
    [InlineData("/Identity/Account/Register")]
    [InlineData("/Identity/Account/ForgotPassword")]
    public async Task Public_auth_pages_render(string path)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        Assert.Contains("__RequestVerificationToken", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/Identity/Account/ForgotPasswordConfirmation", "Check your inbox")]
    [InlineData("/Identity/Account/RegisterConfirmation", "Verify your email")]
    public async Task Confirmation_pages_use_the_illustrated_auth_layout(string path, string heading)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync(path);
        Assert.Contains(heading, html);
        Assert.Contains("auth-recovery-page", html);
        Assert.Contains("/images/auth-pets.svg", html);
        Assert.Contains("http://localhost:8025/", html);
        Assert.DoesNotContain("EmailConfirmationUrl", html);
    }

    [Fact]
    public async Task Incomplete_verification_links_show_a_styled_recovery_screen()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/Identity/Account/ConfirmEmail?userId=invalid&code=invalid&returnUrl=https://example.com");
        Assert.Contains("auth-confirmed", html);
        Assert.Contains("Send a new verification link", html);
        Assert.DoesNotContain("Email verified.", html);
        Assert.DoesNotContain("returnUrl=https", html);
    }

    [Theory]
    [InlineData("Both")]
    [InlineData("PlatformManager")]
    public async Task Registration_service_rejects_forbidden_account_types(string accountType)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RegistrationService>();
        var (result, user) = await service.RegisterAsync("invalid@example.test", "Test!Password123", "Test", "User", accountType);
        Assert.False(result.Succeeded);
        Assert.Null(user);
    }

    [SqlServerFact]
    public async Task Registration_confirmation_login_authorization_recovery_and_profile_work_end_to_end()
    {
        var mail = new Mailbox();
        var clock = new TestClock();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseEnvironment("Development").ConfigureServices(s =>
            {
                s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(mail);
                s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(clock);
            }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"auth-{Guid.NewGuid():N}@example.test";
        const string password = "Original!Password123";
        const string newPassword = "Changed!Password456";
        const string changedPassword = "Updated!Password789";
        try
        {
            using var scope = factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            // Denied before authentication.
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Dashboard/Manager")).StatusCode);
            var fields = new Dictionary<string, string>
            {
                ["Input.FirstName"] = "Auth", ["Input.LastName"] = "Test", ["Input.Email"] = email,
                ["Input.Password"] = password, ["Input.ConfirmPassword"] = password, ["Input.AccountType"] = "PlatformManager"
            };
            Assert.Equal(HttpStatusCode.OK, (await Post(client, "/Identity/Account/Register", fields)).StatusCode);
            Assert.Null(await users.FindByEmailAsync(email));
            fields["Input.AccountType"] = "Both";
            Assert.Equal(HttpStatusCode.OK, (await Post(client, "/Identity/Account/Register", fields)).StatusCode);
            Assert.Null(await users.FindByEmailAsync(email));
            fields["Input.AccountType"] = "Customer";
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/Register", fields)).StatusCode);
            var user = (await users.FindByEmailAsync(email))!;
            Assert.NotNull(user);
            Assert.False(user.EmailConfirmed);
            Assert.True(await users.IsInRoleAsync(user, Roles.Customer));
            Assert.False(await users.IsInRoleAsync(user, Roles.PlatformManager));
            var registrationConfirmation = await client.GetStringAsync("/Identity/Account/RegisterConfirmation");
            Assert.Contains("Verify your email", registrationConfirmation);
            Assert.Contains(email, registrationConfirmation);
            Assert.Contains("data-resend-at", registrationConfirmation);
            var registrationMailCount = mail.Messages.Count;
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Identity/Account/RegisterConfirmation?handler=Resend", new FormUrlEncodedContent([]))).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/RegisterConfirmation?handler=Resend", new())).StatusCode);
            Assert.Equal(registrationMailCount, mail.Messages.Count);
            clock.Advance(TimeSpan.FromSeconds(46));
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/RegisterConfirmation?handler=Resend", new())).StatusCode);
            Assert.Equal(registrationMailCount + 1, mail.Messages.Count);
            var login = new Dictionary<string, string> { ["Input.Email"] = email, ["Input.Password"] = password };
            Assert.Equal(HttpStatusCode.OK, (await Post(client, "/Identity/Account/Login", new()
            { ["Input.Email"] = email, ["Input.Password"] = "Wrong!Password123" })).StatusCode);
            var unverifiedLogin = await Post(client, "/Identity/Account/Login?returnUrl=%2FDashboard%2FCustomer", login);
            Assert.Equal(HttpStatusCode.Redirect, unverifiedLogin.StatusCode);
            Assert.Contains("RegisterConfirmation", unverifiedLogin.Headers.Location!.ToString());
            Assert.Contains("returnUrl=", unverifiedLogin.Headers.Location.ToString());
            var verifyEmail = await client.GetStringAsync(unverifiedLogin.Headers.Location);
            Assert.Contains("Verify your email", verifyEmail);
            Assert.Contains(email, verifyEmail);
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Dashboard/Customer")).StatusCode);
            var confirmationLink = mail.LastLink();
            var confirmedPage = await client.GetStringAsync(confirmationLink);
            Assert.Contains("Email verified.", confirmedPage);
            Assert.Contains("auth-confirmed", confirmedPage);
            var refreshedVerification = await client.GetAsync(unverifiedLogin.Headers.Location);
            Assert.Equal(HttpStatusCode.Redirect, refreshedVerification.StatusCode);
            Assert.Contains("Login", refreshedVerification.Headers.Location!.ToString());
            Assert.Contains("Email verified.", await client.GetStringAsync(confirmationLink));
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/Login", login)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Dashboard/Customer")).StatusCode);
            Assert.Contains("AccessDenied", (await client.GetAsync("/Dashboard/Manager")).Headers.Location!.ToString());
            Assert.Contains("AccessDenied", (await client.GetAsync("/Dashboard/Sitter")).Headers.Location!.ToString());
            foreach (var path in new[] { "Index", "Email", "ChangePassword", "TwoFactorAuthentication", "PersonalData", "EnableAuthenticator", "DeletePersonalData" })
            {
                var page = await client.GetAsync("/Identity/Account/Manage/" + path);
                Assert.True(page.StatusCode == HttpStatusCode.OK, path + ": " + page.StatusCode + " " + page.Headers.Location);
            }
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Identity/Account/Logout", new FormUrlEncodedContent([]))).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/Manage/Index", new()
            { ["Input.FirstName"] = "Updated", ["Input.LastName"] = "Name", ["Input.City"] = "Beirut" })).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/Manage/ChangePassword", new()
            { ["Input.OldPassword"] = password, ["Input.NewPassword"] = newPassword, ["Input.ConfirmPassword"] = newPassword })).StatusCode);
            // Password change refreshes the current session.
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Dashboard/Customer")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/Logout", new())).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Dashboard/Customer")).StatusCode);
            var before = mail.Messages.Count;
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/ForgotPassword", new() { ["Input.Email"] = "unknown@example.test" })).StatusCode);
            Assert.Equal(before, mail.Messages.Count);
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/ForgotPassword", new() { ["Input.Email"] = email })).StatusCode);
            var confirmation = await client.GetStringAsync("/Identity/Account/ForgotPasswordConfirmation");
            Assert.Contains("Check your inbox", confirmation);
            Assert.Contains("2 hours", confirmation);
            Assert.Contains(email, confirmation);
            Assert.Contains("http://localhost:8025/", confirmation);
            var resetCount = mail.Messages.Count;
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Identity/Account/ForgotPasswordConfirmation?handler=Resend", new FormUrlEncodedContent([]))).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/ForgotPasswordConfirmation?handler=Resend", new())).StatusCode);
            Assert.Equal(resetCount, mail.Messages.Count);
            clock.Advance(TimeSpan.FromSeconds(46));
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/ForgotPasswordConfirmation?handler=Resend", new())).StatusCode);
            Assert.Equal(resetCount + 1, mail.Messages.Count);
            var resetLink = mail.LastLink();
            var code = System.Text.Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(QueryHelpers.ParseQuery(new Uri(resetLink).Query)["code"].ToString()));
            var resetFields = new Dictionary<string, string> { ["Input.Email"] = email, ["Input.Code"] = code,
                ["Input.Password"] = changedPassword, ["Input.ConfirmPassword"] = changedPassword };
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, resetLink, resetFields)).StatusCode);
            // Consumed reset tokens cannot be replayed.
            Assert.Equal(HttpStatusCode.OK, (await Post(client, resetLink, resetFields)).StatusCode);
            login["Input.Password"] = newPassword;
            Assert.Equal(HttpStatusCode.OK, (await Post(client, "/Identity/Account/Login", login)).StatusCode);
            login["Input.Password"] = changedPassword;
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/Login", login)).StatusCode);
            // A deactivated account loses its existing session and cannot sign in again.
            using (var deactivate = factory.Services.CreateScope())
            {
                var manager = deactivate.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var current = (await manager.FindByEmailAsync(email))!;
                Assert.Equal("Updated", current.FirstName);
                current.IsActive = false;
                Assert.True((await manager.UpdateAsync(current)).Succeeded);
            }
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Dashboard/Customer")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Post(client, "/Identity/Account/Login", login)).StatusCode);
        }
        finally
        {
            using var cleanup = factory.Services.CreateScope();
            var users = cleanup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(email);
            if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
        }
    }

    [SqlServerFact]
    public async Task Sitter_roles_two_factor_recovery_codes_and_lockout_work()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"auth-{Guid.NewGuid():N}@example.test";
        const string password = "Test!Password12345";
        try
        {
            using (var setup = factory.Services.CreateScope())
            {
                var registration = setup.ServiceProvider.GetRequiredService<RegistrationService>();
                var (result, user) = await registration.RegisterAsync(email, password, "Sitter", "Test", "Sitter");
                Assert.True(result.Succeeded);
                user!.EmailConfirmed = true;
                var users = setup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                Assert.True((await users.UpdateAsync(user)).Succeeded);
            }
            var login = new Dictionary<string, string> { ["Input.Email"] = email, ["Input.Password"] = password };
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/Login", login)).StatusCode);
            Assert.Contains("AccessDenied", (await client.GetAsync("/Dashboard/Customer")).Headers.Location!.ToString());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Dashboard/Sitter")).StatusCode);
            // Initialize and enable the authenticator through the real UI handler.
            await client.GetAsync("/Identity/Account/Manage/EnableAuthenticator");
            string key;
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                key = (await users.GetAuthenticatorKeyAsync((await users.FindByEmailAsync(email))!))!;
            }
            var enable = await Post(client, "/Identity/Account/Manage/EnableAuthenticator", new() { ["Input.Code"] = Totp(key) });
            Assert.Equal(HttpStatusCode.Redirect, enable.StatusCode);
            string recoveryCode;
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = (await users.FindByEmailAsync(email))!;
                Assert.True(await users.GetTwoFactorEnabledAsync(user));
                recoveryCode = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 5))!.First();
            }
            await Post(client, "/Identity/Account/Logout", new());
            var challenge = await Post(client, "/Identity/Account/Login", login);
            Assert.Contains("LoginWith2fa", challenge.Headers.Location!.ToString());
            Assert.Equal(HttpStatusCode.OK, (await Post(client, "/Identity/Account/LoginWith2fa", new() { ["Input.TwoFactorCode"] = "invalid" })).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/LoginWith2fa", new() { ["Input.TwoFactorCode"] = Totp(key) })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Dashboard/Sitter")).StatusCode);
            await Post(client, "/Identity/Account/Logout", new());
            await Post(client, "/Identity/Account/Login", login);
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/LoginWithRecoveryCode", new() { ["Input.RecoveryCode"] = recoveryCode })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Dashboard/Sitter")).StatusCode);
            await Post(client, "/Identity/Account/Logout", new());
            login["Input.Password"] = "Wrong!Password123";
            HttpResponseMessage? last = null;
            for (var attempt = 0; attempt < 5; attempt++) last = await Post(client, "/Identity/Account/Login", login);
            using (var check = factory.Services.CreateScope())
            {
                var userManager = check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var current = (await userManager.FindByEmailAsync(email))!;
                Assert.True(last!.Headers.Location?.ToString().Contains("Lockout") == true,
                    $"Lockout response: {last.StatusCode}, failed attempts: {current.AccessFailedCount}, enabled: {current.LockoutEnabled}, until: {current.LockoutEnd}");
            }
            using var verify = factory.Services.CreateScope();
            var manager = verify.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True(await manager.IsLockedOutAsync((await manager.FindByEmailAsync(email))!));
        }
        finally
        {
            using var cleanup = factory.Services.CreateScope();
            var users = cleanup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(email);
            if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
        }
    }

    private static string Totp(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(key.Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var secret = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = System.Security.Cryptography.HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 15;
        var value = ((hash[offset] & 127) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1000000).ToString("D6");
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var getPath = path.EndsWith("/Logout", StringComparison.Ordinal) ? "/Identity/Account/Manage/Index" : path;
        var html = await client.GetStringAsync(getPath);
        var token = Regex.Match(html, "<input(?=[^>]*name=\"__RequestVerificationToken\")(?=[^>]*value=\"([^\"]+)\")[^>]*>").Groups[1].Value;
        Assert.NotEmpty(token);
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(token);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
}
