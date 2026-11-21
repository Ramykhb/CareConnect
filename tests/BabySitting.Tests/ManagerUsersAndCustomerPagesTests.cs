using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BabySitting.Controllers;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Accounts;
using BabySitting.ViewModels;
using Xunit;

namespace BabySitting.Tests;

// #18 manager user management and #13 the owner/parent Find Sitters, Payments and Reviews pages.
public sealed class ManagerUsersAndCustomerPagesTests
{
    private sealed class Mailbox : IEmailSender
    {
        public ConcurrentQueue<(string To, string Html)> Sent { get; } = new();
        public Task SendEmailAsync(string email, string subject, string htmlMessage) { Sent.Enqueue((email, htmlMessage)); return Task.CompletedTask; }
    }
    private static WebApplicationFactory<Program> Factory(Mailbox? mail = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development")
        .ConfigureServices(s => { if (mail is not null) { s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(mail); } }));

    [Theory]
    [InlineData("/ManagerUsers")]
    [InlineData("/ManagerUsers/AddManager")]
    [InlineData("/find-sitters")]
    [InlineData("/my-payments")]
    [InlineData("/my-reviews")]
    public async Task Pages_require_sign_in(string path)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Identity/Account/Login", response.Headers.Location!.ToString());
    }

    [SqlServerFact]
    public async Task Managers_find_users_switch_them_off_and_on_and_add_managers()
    {
        var mail = new Mailbox();
        await using var factory = Factory(mail);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var tag = "Mgr" + Guid.NewGuid().ToString("N")[..10];
        async Task<ApplicationUser> Create(string first, string? role)
        {
            var u = new ApplicationUser { Id = Guid.NewGuid(), FirstName = first, LastName = tag, UserName = tag + first + "@example.test", Email = tag + first + "@example.test", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(u)).Succeeded);
            if (role is not null) Assert.True((await users.AddToRoleAsync(u, role)).Succeeded);
            return u;
        }
        var manager = await Create("Boss", Roles.PlatformManager);
        var customer = await Create("Busy", Roles.Customer);
        var sitter = await Create("Sit", Roles.Sitter);
        var idle = await Create("Idle", Roles.Customer);
        var profile = new BabysitterProfile { UserId = sitter.Id, Bio = "Test", City = "Beirut", HourlyRate = 10 };
        var start = DateTimeOffset.UtcNow.AddDays(3);
        var request = new ChildcareRequest { CustomerId = customer.Id, Title = "Busy care", Description = "Test", City = "Beirut", StartsAtUtc = start, EndsAtUtc = start.AddHours(2), Budget = 20, Status = RequestStatus.Booked };
        var offer = new BabysittingOffer { Request = request, SitterProfile = profile, Amount = 20, Status = OfferStatus.Accepted };
        var booking = new BabysittingBooking { Request = request, Offer = offer, CustomerId = customer.Id, SitterProfile = profile, StartsAtUtc = start, EndsAtUtc = start.AddHours(2), TotalAmount = 20, Status = BookingStatus.Confirmed };
        db.AddRange(profile, request, offer, booking);
        await db.SaveChangesAsync();
        var added = tag + "new@example.test";
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<IManagerUserService>();
            var page = await service.ListAsync(tag, "all", "all", 1);
            Assert.Equal(4, page.Total);
            Assert.Single((await service.ListAsync(tag, Roles.Sitter, "all", 1)).Users);
            string Version(Guid id) { db.ChangeTracker.Clear(); return db.Users.AsNoTracking().Single(u => u.Id == id).ConcurrencyStamp!; }

            Assert.False((await service.SetActiveAsync(manager.Id, manager.Id, false, Version(manager.Id))).Succeeded);       // not yourself
            var busy = await service.SetActiveAsync(manager.Id, customer.Id, false, Version(customer.Id));
            Assert.False(busy.Succeeded);
            Assert.Contains("active bookings", busy.Message);
            Assert.False((await service.SetActiveAsync(manager.Id, idle.Id, false, "stale")).Succeeded);                     // stale page
            var stamp = db.Users.AsNoTracking().Single(u => u.Id == idle.Id).SecurityStamp;
            Assert.True((await service.SetActiveAsync(manager.Id, idle.Id, false, Version(idle.Id))).Succeeded);
            db.ChangeTracker.Clear();
            var off = db.Users.AsNoTracking().Single(u => u.Id == idle.Id);
            Assert.False(off.IsActive);
            Assert.NotEqual(stamp, off.SecurityStamp);                                                                       // signed out everywhere
            Assert.Single((await service.ListAsync(tag, "all", "inactive", 1)).Users);
            Assert.True((await service.SetActiveAsync(manager.Id, idle.Id, true, Version(idle.Id))).Succeeded);

            var result = await service.AddManagerAsync(new AddManagerForm { FirstName = "New", LastName = tag, Email = added });
            Assert.True(result.Succeeded, result.Message);
            var created = await users.FindByEmailAsync(added);
            Assert.NotNull(created);
            Assert.True(await users.IsInRoleAsync(created!, Roles.PlatformManager));
            Assert.False(await users.HasPasswordAsync(created!));                                                            // they choose it from the link
            var sent = Assert.Single(mail.Sent, m => m.To == added);
            Assert.Contains("/Identity/Account/ResetPassword", sent.Html);
            Assert.False((await service.AddManagerAsync(new AddManagerForm { FirstName = "Dup", LastName = tag, Email = added })).Succeeded);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.BabysittingBookings.Where(b => b.Id == booking.Id).ExecuteDeleteAsync();
            await db.BabysittingOffers.Where(o => o.Id == offer.Id).ExecuteDeleteAsync();
            await db.ChildcareRequests.Where(r => r.Id == request.Id).ExecuteDeleteAsync();
            await db.BabysitterProfiles.Where(p => p.Id == profile.Id).ExecuteDeleteAsync();
            var ids = await db.Users.Where(u => u.LastName == tag).Select(u => u.Id).ToListAsync();
            await db.Notifications.Where(n => ids.Contains(n.UserId)).ExecuteDeleteAsync();
            await db.UserRoles.Where(r => ids.Contains(r.UserId)).ExecuteDeleteAsync();
            // Other tests may notify this manager at any moment (for example about a sitter to verify), so retry.
            for (var attempt = 1; ; attempt++)
            {
                await db.Notifications.Where(n => ids.Contains(n.UserId)).ExecuteDeleteAsync();
                try { await db.Users.Where(u => ids.Contains(u.Id)).ExecuteDeleteAsync(); break; }
                catch (Microsoft.Data.SqlClient.SqlException) when (attempt < 5) { }
            }
        }
    }

    [SqlServerFact]
    public async Task Customer_pages_show_sitters_payments_and_reviews()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var city = "Pages-" + Guid.NewGuid().ToString("N")[..8];
        ApplicationUser NewUser(string name) => new() { Id = Guid.NewGuid(), FirstName = name, LastName = "Pages", UserName = "pages-" + Guid.NewGuid().ToString("N"), City = city };
        var customer = NewUser("Customer"); var sitter = NewUser("Nour");
        var profile = new BabysitterProfile { User = sitter, Bio = "Calm and experienced", City = city, HourlyRate = 12, YearsOfExperience = 4 };
        profile.Services.Add(new SitterService { ServiceType = Enum.GetValues<ServiceType>()[0] });
        var past = DateTimeOffset.UtcNow.AddDays(-10);
        BabysittingBooking Done(string title)
        {
            var r = new ChildcareRequest { Customer = customer, Title = title, Description = "Test", City = city, StartsAtUtc = past, EndsAtUtc = past.AddHours(2), Budget = 30, Status = RequestStatus.Completed };
            return new BabysittingBooking { Request = r, Offer = new BabysittingOffer { Request = r, SitterProfile = profile, Amount = 30, Status = OfferStatus.Accepted }, Customer = customer, SitterProfile = profile,
                StartsAtUtc = past, EndsAtUtc = past.AddHours(2), TotalAmount = 30, Status = BookingStatus.Completed, CompletedAtUtc = past.AddHours(2) };
        }
        var reviewed = Done("Reviewed care"); var unreviewed = Done("Not reviewed yet");
        var payment = new Payment { Booking = reviewed, PayerId = customer.Id, Amount = 30, Currency = "USD", Status = PaymentStatus.Succeeded, Provider = "Dummy",
            ProviderTransactionId = "P-" + Guid.NewGuid().ToString("N")[..8], IdempotencyKey = Guid.NewGuid(), PaidAtUtc = past };
        var review = new Review { Booking = reviewed, ReviewerId = customer.Id, Rating = 5, Comment = "Wonderful", CreatedAtUtc = past.AddDays(1) };
        db.AddRange(customer, sitter, profile, reviewed, unreviewed, payment, review);
        await db.SaveChangesAsync();
        try
        {
            var controller = new CustomerPagesController(db, users, TimeProvider.System)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, customer.Id.ToString())], "Test")) } }
            };
            var find = Assert.IsType<FindSittersViewModel>(Assert.IsType<ViewResult>(await controller.FindSitters(null, null, null)).Model);
            var card = Assert.Single(find.Sitters);                                                         // defaults to the customer's city
            Assert.Equal(5.0, card.Rating);
            Assert.Empty(Assert.IsType<FindSittersViewModel>(Assert.IsType<ViewResult>(await controller.FindSitters(city, null, "nobody-matches")).Model).Sitters);

            var pay = Assert.IsType<CustomerPaymentsViewModel>(Assert.IsType<ViewResult>(await controller.Payments()).Model);
            var row = Assert.Single(pay.Payments);
            Assert.True(row.Receipt);
            Assert.Equal("Reviewed care", row.Title);

            var rev = Assert.IsType<CustomerReviewsViewModel>(Assert.IsType<ViewResult>(await controller.Reviews()).Model);
            Assert.Equal("Wonderful", Assert.Single(rev.Reviews).Comment);
            Assert.Equal("Not reviewed yet", Assert.Single(rev.Awaiting).Title);
        }
        finally
        {
            db.ChangeTracker.Clear();
            var bookingIds = new[] { reviewed.Id, unreviewed.Id };
            var requestIds = new[] { reviewed.RequestId, unreviewed.RequestId };
            await db.Reviews.Where(r => bookingIds.Contains(r.BookingId)).ExecuteDeleteAsync();
            await db.Payments.Where(p => bookingIds.Contains(p.BookingId)).ExecuteDeleteAsync();
            await db.BabysittingBookings.Where(b => bookingIds.Contains(b.Id)).ExecuteDeleteAsync();
            await db.BabysittingOffers.Where(o => requestIds.Contains(o.RequestId)).ExecuteDeleteAsync();
            await db.ChildcareRequests.Where(r => requestIds.Contains(r.Id)).ExecuteDeleteAsync();
            await db.SitterServices.Where(s => s.SitterProfileId == profile.Id).ExecuteDeleteAsync();
            await db.BabysitterProfiles.Where(p => p.Id == profile.Id).ExecuteDeleteAsync();
            await db.Notifications.Where(n => n.UserId == customer.Id || n.UserId == sitter.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == customer.Id || u.Id == sitter.Id).ExecuteDeleteAsync();
        }
    }
}
