using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PetSitting.Models;
using PetSitting.Services.Auth;
using Xunit;

namespace PetSitting.Tests;

public sealed class GoogleAuthTests
{
    private const string Account = "/Identity/Account/";
    private static WebApplicationFactory<Program> Factory(bool configured = true, bool verified = true, string? email = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Authentication:Google:ClientId", configured ? "test-client" : "");
            builder.UseSetting("Authentication:Google:ClientSecret", configured ? "test-secret" : "");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Google:ClientId"] = configured ? "test-client" : "",
                ["Authentication:Google:ClientSecret"] = configured ? "test-secret" : ""
            }));
            builder.ConfigureServices(services => services.PostConfigure<GoogleOptions>("Google", options =>
                options.Backchannel = new HttpClient(new GoogleBackchannel(verified, email ?? "google-test@example.test"))));
        });
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost:5101") });

    [Fact]
    public async Task Unconfigured_google_is_hidden_and_cannot_be_challenged()
    {
        await using var factory = Factory(configured: false); using var client = Client(factory);
        var html = await client.GetStringAsync(Account + "Login");
        Assert.DoesNotContain("Continue with Google</button>", html);
        var response = await Post(client, Account + "ExternalLogin?handler=Challenge", new());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Challenge_requires_csrf_and_uses_the_apps_exact_callback()
    {
        await using var factory = Factory(); using var client = Client(factory);
        var denied = await client.PostAsync(Account + "ExternalLogin?handler=Challenge", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        var response = await Post(client, Account + "ExternalLogin?handler=Challenge&returnUrl=https://evil.example", new());
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("accounts.google.com", location.Host);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("test-client", query["client_id"]);
        Assert.Equal("http://localhost:5101/signin-google", query["redirect_uri"]);
        Assert.DoesNotContain("test-secret", location.ToString());
        using var scope = factory.Services.CreateScope();
        var properties = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<GoogleOptions>>().Get("Google").StateDataFormat.Unprotect(query["state"]!);
        Assert.NotNull(properties);
        Assert.DoesNotContain("evil.example", properties.RedirectUri!);
    }

    [Fact]
    public async Task Google_callback_maps_verified_identity_into_the_external_cookie()
    {
        await using var factory = Factory(); using var client = Client(factory);
        var response = await Callback(client);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("handler=Callback", response.Headers.Location!.ToString());
        var cookieName = "CareConnect.PetSitting.Identity.External";
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(cookieName + "=", StringComparison.Ordinal));
        var value = Uri.UnescapeDataString(cookie.Split(';')[0][(cookieName.Length + 1)..]);
        var ticket = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ExternalScheme).TicketDataFormat.Unprotect(value);
        Assert.NotNull(ticket);
        Assert.Equal("google-test@example.test", ticket.Principal.FindFirstValue(ClaimTypes.Email));
        Assert.Equal("True", ticket.Principal.FindFirstValue("google:email_verified"), ignoreCase: true);
        Assert.Equal("google-subject", ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task Unverified_google_email_and_tampered_state_are_rejected()
    {
        await using var factory = Factory(verified: false); using var client = Client(factory);
        var callback = await Callback(client);
        var rejected = await client.GetAsync(callback.Headers.Location);
        Assert.Contains("googleError=True", rejected.Headers.Location!.ToString());
        var tampered = await client.GetAsync("/signin-google?code=test&state=invalid");
        Assert.Contains("googleError=true", tampered.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Signup_cannot_be_posted_without_a_google_ticket()
    {
        await using var factory = Factory(); using var client = Client(factory);
        var response = await Post(client, Account + "ExternalLogin?handler=Confirmation", new()
        { ["Input.FirstName"] = "Test", ["Input.LastName"] = "User", ["Input.AccountType"] = "Customer" });
        Assert.Contains("googleError=True", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("PlatformManager", true)]
    [InlineData("Both", true)]
    [InlineData("Customer", false)]
    public async Task Registration_rejects_role_tampering_and_unverified_email(string role, bool verified)
    {
        await using var factory = Factory(); using var scope = factory.Services.CreateScope();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        { new Claim(ClaimTypes.Email, "test@example.test"), new Claim("google:email_verified", verified.ToString()) }, "Google"));
        var (result, user) = await scope.ServiceProvider.GetRequiredService<RegistrationService>()
            .RegisterGoogleAsync(new ExternalLoginInfo(principal, "Google", "subject", "Google"), "Test", "User", role);
        Assert.False(result.Succeeded); Assert.Null(user);
    }

    [SqlServerFact]
    public async Task Google_signup_creates_one_role_and_login_and_subsequent_login_reuses_the_account()
    {
        foreach (var role in new[] { "Customer", "Sitter" })
        {
            var email = "google-" + Guid.NewGuid().ToString("N") + "@example.test";
            await using var factory = Factory(email: email); using var client = Client(factory);
            try
            {
                var callback = await Callback(client);
                var page = await client.GetAsync(callback.Headers.Location);
                var html = await page.Content.ReadAsStringAsync();
                Assert.Contains("Finish your account", html);
                var response = await Post(client, Account + "ExternalLogin?handler=Confirmation", new()
                { ["Input.FirstName"] = "Google", ["Input.LastName"] = "Test", ["Input.AccountType"] = role }, html);
                Assert.Equal("/Dashboard", response.Headers.Location!.ToString());
                using var scope = factory.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByEmailAsync(email); Assert.NotNull(user); Assert.True(user.EmailConfirmed);
                Assert.False(await users.HasPasswordAsync(user));
                Assert.Equal(new[] { role == "Customer" ? Roles.Customer : Roles.Sitter }, await users.GetRolesAsync(user));
                Assert.Single(await users.GetLoginsAsync(user));
                // Another browser returns directly to the dashboard, without creating another account.
                using var returning = Client(factory); var again = await Callback(returning);
                var loggedIn = await returning.GetAsync(again.Headers.Location);
                Assert.Equal("/Dashboard", loggedIn.Headers.Location!.ToString());
                Assert.Equal(user.Id, (await users.FindByEmailAsync(email))!.Id);
                user.IsActive = false; Assert.True((await users.UpdateAsync(user)).Succeeded);
                using var inactive = Client(factory); var blockedCallback = await Callback(inactive);
                var blocked = await inactive.GetAsync(blockedCallback.Headers.Location);
                Assert.Contains("googleError=True", blocked.Headers.Location!.ToString());
                user.IsActive = true; Assert.True((await users.UpdateAsync(user)).Succeeded);
                Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(5))).Succeeded);
                using var locked = Client(factory); var lockedCallback = await Callback(locked);
                var lockout = await locked.GetAsync(lockedCallback.Headers.Location);
                Assert.Contains("Lockout", lockout.Headers.Location!.ToString());
                Assert.True((await users.SetLockoutEndDateAsync(user, null)).Succeeded);
                Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
                using var mfa = Client(factory); var mfaCallback = await Callback(mfa);
                var challenge = await mfa.GetAsync(mfaCallback.Headers.Location);
                Assert.Contains("LoginWith2fa", challenge.Headers.Location!.ToString());
            }
            finally
            {
                using var cleanup = factory.Services.CreateScope(); var users = cleanup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByEmailAsync(email); if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
        }
    }

    private static async Task<HttpResponseMessage> Callback(HttpClient client)
    {
        var challenge = await Post(client, Account + "ExternalLogin?handler=Challenge", new());
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
        return await client.GetAsync("/signin-google?code=test&state=" + Uri.EscapeDataString(state));
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields, string? html = null)
    {
        html ??= await client.GetStringAsync(Account + "Login");
        var token = Regex.Match(html, "<input(?=[^>]*name=\"__RequestVerificationToken\")(?=[^>]*value=\"([^\"]+)\")[^>]*>").Groups[1].Value;
        Assert.NotEmpty(token); fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(token);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
    private sealed class GoogleBackchannel(bool verified, string email) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.Contains("token", StringComparison.Ordinal)
                ? "{\"access_token\":\"test-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}"
                : System.Text.Json.JsonSerializer.Serialize(new { sub = "google-subject", email, email_verified = verified, given_name = "Google", family_name = "Test", name = "Google Test" });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
