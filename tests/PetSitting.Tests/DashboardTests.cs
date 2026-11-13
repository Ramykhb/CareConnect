using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Dashboard;
using PetSitting.ViewModels;
using Xunit;

namespace PetSitting.Tests;

public sealed class DashboardTests
{
    private static readonly Guid AccountId = Guid.Parse("e496c4c5-215c-4e95-a55c-2ce253ad966c");

    private sealed class DashboardAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Dashboard-Test-Role"].ToString();
            if (string.IsNullOrEmpty(role)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, AccountId.ToString()), new Claim(ClaimTypes.Role, role)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class DashboardUsers : IUserStore<ApplicationUser>
    {
        public Task<ApplicationUser?> FindByIdAsync(string id, CancellationToken ct) => Task.FromResult<ApplicationUser?>(new() { Id = AccountId, FirstName = "Sarah", LastName = "Haddad", City = "Beirut" });
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.UserName);
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);
        public Task SetUserNameAsync(ApplicationUser user, string? name, CancellationToken ct) => Task.CompletedTask;
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? name, CancellationToken ct) => Task.CompletedTask;
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<ApplicationUser?> FindByNameAsync(string name, CancellationToken ct) => Task.FromResult<ApplicationUser?>(null);
        public void Dispose() { }
    }

    private sealed class DashboardFixture(bool populated) : IDashboardService
    {
        public Task<DashboardViewModel> LoadAsync(ApplicationUser account, bool sitter, CancellationToken ct = default)
        {
            var now = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
            account.FirstName = sitter ? "Omar" : "Sarah";
            account.LastName = sitter ? "Khalil" : "Haddad";
            var sitterUser = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Omar", LastName = "Khalil" };
            var profile = new PetSitterProfile { User = sitterUser, City = "Beirut", HourlyRate = 16, YearsOfExperience = 3 };
            var pets = new[] { new Pet { Name = "Max", Species = "Dog", Breed = "Golden Retriever", AgeYears = 3 }, new Pet { Name = "Luna", Species = "Cat", Breed = "British Shorthair", AgeYears = 5 }, new Pet { Name = "Bella", Species = "Dog", Breed = "Beagle", AgeYears = 2 } };
            var requests = pets.Select((pet, i) => new PetCareRequest { Title = "Care for " + pet.Name, Customer = new() { FirstName = "Sarah", LastName = "Haddad" }, City = "Beirut", StartsAtUtc = now.AddDays(i + 1), EndsAtUtc = now.AddDays(i + 2), Budget = i == 0 ? 135 : 45, Description = "A little company, a walk, and their favorite treats.", ServiceType = (ServiceType)i, Pets = [new() { Pet = pet, PetId = pet.Id }] }).ToArray();
            var bookings = Enumerable.Range(1, 12).Select(month => new PetBooking { Customer = account, SitterProfile = profile, Request = requests[month % 3], StartsAtUtc = new DateTimeOffset(2026, month, 12, 8, 0, 0, TimeSpan.Zero), EndsAtUtc = new DateTimeOffset(2026, month, 13, 8, 0, 0, TimeSpan.Zero), TotalAmount = 135, Status = month < 11 ? BookingStatus.Completed : month == 11 ? BookingStatus.InProgress : BookingStatus.Confirmed, Payments = [new() { Status = PaymentStatus.Succeeded, Amount = 100 + month * 40, PaidAtUtc = new DateTimeOffset(2026, month, 12, 8, 0, 0, TimeSpan.Zero) }] }).ToArray();
            return Task.FromResult(new DashboardViewModel { Account = account, IsSitter = sitter, Profile = sitter && populated ? profile : null, Now = now, Pets = populated ? pets : [], Requests = populated ? requests : [], Bookings = populated ? bookings : [], Completed = populated ? 214 : 0, Unread = populated ? 5 : 0, AcceptedOffers = populated ? 49 : 0, DeclinedOffers = populated ? 1 : 0, Sitters = populated && !sitter ? [profile] : [], Notifications = populated ? [new() { Title = "Omar accepted your request", Body = "Overnight care · Max · 20–22 Nov", CreatedAtUtc = now }, new() { Title = "Payment received", Body = "$45 · Drop-in visit for Luna", CreatedAtUtc = now.AddHours(-1) }, new() { Title = "New review", Body = "A little love from a pet parent.", CreatedAtUtc = now.AddDays(-1) }] : [] });
        }
    }

    private static WebApplicationFactory<Program> Factory(bool populated = false) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development").ConfigureServices(services =>
    {
        services.RemoveAll<IUserStore<ApplicationUser>>();
        services.AddScoped<IUserStore<ApplicationUser>, DashboardUsers>();
        services.RemoveAll<IDashboardService>();
        services.AddSingleton<IDashboardService>(new DashboardFixture(populated));
        services.AddAuthentication(options => options.DefaultAuthenticateScheme = "DashboardTests").AddScheme<AuthenticationSchemeOptions, DashboardAuthentication>("DashboardTests", _ => { });
    }));

    [Theory]
    [InlineData("/Dashboard", null, HttpStatusCode.Redirect)]
    [InlineData("/Dashboard/Customer", null, HttpStatusCode.Redirect)]
    [InlineData("/Dashboard/Sitter", null, HttpStatusCode.Redirect)]
    [InlineData("/Dashboard/Customer", Roles.Sitter, HttpStatusCode.Redirect)]
    [InlineData("/Dashboard/Sitter", Roles.Customer, HttpStatusCode.Redirect)]
    public async Task Dashboard_enforces_authentication_and_role_access(string path, string? role, HttpStatusCode expected)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", role);
        using var response = await client.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        Assert.Contains(role is null ? "Login" : "AccessDenied", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData(Roles.Customer, "/Dashboard/Customer")]
    [InlineData(Roles.Sitter, "/Dashboard/Sitter")]
    public async Task Dashboard_hub_redirects_to_the_accounts_role(string role, string expected)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", role);
        using var response = await client.GetAsync("/Dashboard");
        Assert.Equal(expected, response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Dashboard_renders_empty_and_populated_accounts(bool sitter, bool populated)
    {
        await using var factory = Factory(populated);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", sitter ? Roles.Sitter : Roles.Customer);
        using var response = await client.GetAsync(sitter ? "/Dashboard/Sitter" : "/Dashboard/Customer");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains(sitter ? "Welcome back," : "Good morning,", html);
        Assert.Contains(sitter ? "Omar" : "Sarah", html);
        Assert.Contains(sitter ? "Revenue" : "Statistics", html);
        Assert.Contains("__RequestVerificationToken", html);
        if (populated) Assert.Contains("Max", html);
        else Assert.Contains(sitter ? "Your first payment" : "Add your first pet", html);
        // Optional local browser QA artifacts; never used by the application.
        var output = Environment.GetEnvironmentVariable("CARECONNECT_DASHBOARD_QA");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"{(sitter ? "sitter" : "owner")}{(populated ? "" : "-empty")}.html"), html);
        }
    }

    [Fact]
    public void Analytics_use_Beirut_calendar_boundaries_and_successful_USD_payments()
    {
        // This UTC timestamp is already January 1 in Beirut.
        var now = new DateTimeOffset(2026, 12, 31, 22, 30, 0, TimeSpan.Zero);
        var current = new PetBooking
        {
            StartsAtUtc = now.AddMinutes(-15), EndsAtUtc = now.AddMinutes(105), Status = BookingStatus.Confirmed,
            Payments = [new() { Amount = 15, Currency = "USD", Status = PaymentStatus.Succeeded, PaidAtUtc = now },
                new() { Amount = 90, Currency = "USD", Status = PaymentStatus.Pending, CreatedAtUtc = now },
                new() { Amount = 70, Currency = "EUR", Status = PaymentStatus.Succeeded, PaidAtUtc = now },
                new() { Amount = 100, Currency = "USD", Status = PaymentStatus.Succeeded, PaidAtUtc = now.AddHours(-1) }]
        };
        var cancelled = new PetBooking { StartsAtUtc = now, EndsAtUtc = now.AddHours(4), Status = BookingStatus.Cancelled };
        var model = new DashboardViewModel { Now = now, Bookings = [current, cancelled] };
        Assert.Equal(2027, model.ReportYear);
        Assert.Equal(1, model.ReportMonth);
        Assert.Equal(1, model.ThisMonth);
        Assert.Same(current, Assert.Single(model.Upcoming));
        Assert.Same(current, Assert.Single(model.Today));
        Assert.Equal(15m, model.TotalPaid);
        Assert.Equal(15m, model.MonthPaid);
        var january = model.Months.First();
        Assert.Equal(2d, january.Hours);
        Assert.Equal(15m, january.Paid);
    }

    [SqlServerFact]
    public async Task Dashboard_queries_keep_owner_data_and_sitter_requests_scoped()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        var dashboard = scope.ServiceProvider.GetRequiredService<IDashboardService>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var city = "dashboard-" + Guid.NewGuid().ToString("N");
            var owner = new ApplicationUser { FirstName = "Owner", LastName = "One", City = city };
            var other = new ApplicationUser { FirstName = "Owner", LastName = "Two", City = "Other" };
            var sitter = new ApplicationUser { FirstName = "Sitter", LastName = "One", City = city };
            db.Users.AddRange(owner, other, sitter);
            db.Pets.AddRange(new Pet { Owner = owner, Name = "Private pet", Species = "Dog" }, new Pet { Owner = other, Name = "Other pet", Species = "Cat" });
            db.PetCareRequests.AddRange(new PetCareRequest { Customer = owner, Title = "Local request", Description = "Care", City = city, StartsAtUtc = DateTimeOffset.UtcNow.AddDays(1), EndsAtUtc = DateTimeOffset.UtcNow.AddDays(2) }, new PetCareRequest { Customer = other, Title = "Other city request", Description = "Care", City = "Other", StartsAtUtc = DateTimeOffset.UtcNow.AddDays(1), EndsAtUtc = DateTimeOffset.UtcNow.AddDays(2) });
            await db.SaveChangesAsync();
            var ownerDashboard = await dashboard.LoadAsync(owner, false);
            Assert.Equal("Private pet", Assert.Single(ownerDashboard.Pets).Name);
            Assert.Equal("Local request", Assert.Single(ownerDashboard.Requests).Title);
            var sitterDashboard = await dashboard.LoadAsync(sitter, true);
            Assert.Empty(sitterDashboard.Pets);
            Assert.Empty(sitterDashboard.Bookings);
            Assert.Equal("Local request", Assert.Single(sitterDashboard.Requests).Title);
        });
    }
}
