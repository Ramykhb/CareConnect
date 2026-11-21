using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Accounts;
using Xunit;

namespace BabySitting.Tests;

// Accounts with care records are closed by a platform manager instead of being deleted.
public sealed class AccountClosureTests
{
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));

    [Theory]
    [InlineData("/AccountClosures")]
    [InlineData("/Dashboard/Manager")]
    public async Task Manager_pages_require_sign_in(string path)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Identity/Account/Login", response.Headers.Location!.ToString());
    }

    [SqlServerFact]
    public async Task Closure_requests_are_reviewed_and_approval_keeps_care_records_but_removes_the_person()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser NewUser(string name) => new() { Id = Guid.NewGuid(), FirstName = name, LastName = "Closure", City = "Beirut", PhoneNumber = "+961 1 234567", UserName = "closure-" + Guid.NewGuid().ToString("N") + "@example.test", Email = "closure-" + Guid.NewGuid().ToString("N") + "@example.test" };
        var customer = NewUser("Customer"); var sitter = NewUser("Sitter"); var manager = NewUser("Manager");
        foreach (var u in new[] { customer, sitter, manager }) Assert.True((await users.CreateAsync(u)).Succeeded);
        Assert.True((await users.AddToRoleAsync(manager, Roles.PlatformManager)).Succeeded);
        var profile = new BabysitterProfile { UserId = sitter.Id, Bio = "Test", City = "Beirut", HourlyRate = 10 };
        var start = DateTimeOffset.UtcNow.AddDays(2);
        var booked = new ChildcareRequest { CustomerId = customer.Id, Title = "Booked care", Description = "Test", City = "Beirut", StartsAtUtc = start, EndsAtUtc = start.AddHours(3), Budget = 30, Status = RequestStatus.Booked };
        var open = new ChildcareRequest { CustomerId = customer.Id, Title = "Open care", Description = "Test", City = "Beirut", StartsAtUtc = start.AddDays(3), EndsAtUtc = start.AddDays(3).AddHours(3), Budget = 30, Status = RequestStatus.Open };
        var offer = new BabysittingOffer { Request = booked, SitterProfile = profile, Amount = 30, Status = OfferStatus.Accepted };
        var booking = new BabysittingBooking { Request = booked, Offer = offer, CustomerId = customer.Id, SitterProfile = profile, StartsAtUtc = start, EndsAtUtc = start.AddHours(3), TotalAmount = 30, Status = BookingStatus.Confirmed };
        db.AddRange(profile, booked, open, offer, booking);
        await db.SaveChangesAsync();
        var service = new AccountClosureService(db, users, TimeProvider.System);
        try
        {
            // Request: one pending request per account, and every manager is told.
            Assert.True((await service.RequestAsync(customer.Id, "Moving abroad")).Succeeded);
            Assert.True((await service.RequestAsync(customer.Id, "Again")).Succeeded);
            Assert.Equal(1, await db.AccountClosureRequests.CountAsync(r => r.UserId == customer.Id && r.Status == ClosureStatus.Pending));
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == manager.Id && n.Title == "Account closure requested"));

            // Withdraw, then request again.
            Assert.True((await service.WithdrawAsync(customer.Id)).Succeeded);
            Assert.Null(await service.PendingForUserAsync(customer.Id));
            Assert.True((await service.RequestAsync(customer.Id, null)).Succeeded);
            var request = (await service.PendingForUserAsync(customer.Id))!;
            string Version() => Convert.ToBase64String(db.AccountClosureRequests.AsNoTracking().Single(r => r.Id == request.Id).RowVersion);

            // Approval waits for active bookings, and is refused with a stale page.
            var blocked = await service.ApproveAsync(manager.Id, request.Id, null, Version());
            Assert.False(blocked.Succeeded);
            Assert.Contains("active booking", blocked.Message);
            Assert.False((await service.ApproveAsync(manager.Id, request.Id, null, Convert.ToBase64String(new byte[8]))).Succeeded);
            await db.BabysittingBookings.Where(b => b.Id == booking.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Completed));

            // Declining needs a note for the user.
            Assert.False((await service.RejectAsync(manager.Id, request.Id, "  ", Version())).Succeeded);

            var approved = await service.ApproveAsync(manager.Id, request.Id, "Done", Version());
            Assert.True(approved.Succeeded, approved.Message);
            db.ChangeTracker.Clear();
            var closed = await db.Users.AsNoTracking().SingleAsync(u => u.Id == customer.Id);
            Assert.False(closed.IsActive);
            Assert.Equal("Closed", closed.FirstName);
            Assert.EndsWith("@closed.invalid", closed.Email);
            Assert.Null(closed.PhoneNumber); Assert.Null(closed.City);
            Assert.True(closed.LockoutEnd > DateTimeOffset.UtcNow.AddYears(100));
            Assert.True(await db.BabysittingBookings.AnyAsync(b => b.Id == booking.Id));                       // care history kept
            Assert.Equal(RequestStatus.Cancelled, (await db.ChildcareRequests.SingleAsync(r => r.Id == open.Id)).Status);
            Assert.Equal(ClosureStatus.Approved, (await db.AccountClosureRequests.SingleAsync(r => r.Id == request.Id)).Status);
            Assert.False((await service.ApproveAsync(manager.Id, request.Id, null, Version())).Succeeded); // already decided

            // Declining another request notifies that user; managers can't approve their own.
            Assert.True((await service.RequestAsync(sitter.Id, null)).Succeeded);
            var sitterRequest = (await service.PendingForUserAsync(sitter.Id))!;
            Assert.True((await service.RejectAsync(manager.Id, sitterRequest.Id, "You still have payouts to settle.", Version2(sitterRequest.Id))).Succeeded);
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == sitter.Id && n.Body == "You still have payouts to settle."));
            Assert.True((await service.RequestAsync(manager.Id, null)).Succeeded);
            var own = (await service.PendingForUserAsync(manager.Id))!;
            Assert.False((await service.ApproveAsync(manager.Id, own.Id, null, Version2(own.Id))).Succeeded);
        }
        finally
        {
            db.ChangeTracker.Clear();
            var ids = new[] { customer.Id, sitter.Id, manager.Id };
            await db.AccountClosureRequests.Where(r => ids.Contains(r.UserId)).ExecuteDeleteAsync();
            await db.Notifications.Where(n => ids.Contains(n.UserId)).ExecuteDeleteAsync();
            await db.BabysittingBookings.Where(b => b.Id == booking.Id).ExecuteDeleteAsync();
            await db.BabysittingOffers.Where(o => o.Id == offer.Id).ExecuteDeleteAsync();
            await db.ChildcareRequests.Where(r => r.Id == booked.Id || r.Id == open.Id).ExecuteDeleteAsync();
            await db.BabysitterProfiles.Where(p => p.Id == profile.Id).ExecuteDeleteAsync();
            await db.UserRoles.Where(r => ids.Contains(r.UserId)).ExecuteDeleteAsync();
            // Other tests may notify this manager at any moment (for example about a sitter to verify), so retry.
            for (var attempt = 1; ; attempt++)
            {
                await db.Notifications.Where(n => ids.Contains(n.UserId)).ExecuteDeleteAsync();
                try { await db.Users.Where(u => ids.Contains(u.Id)).ExecuteDeleteAsync(); break; }
                catch (Microsoft.Data.SqlClient.SqlException) when (attempt < 5) { }
            }
        }

        string Version2(Guid id) => Convert.ToBase64String(db.AccountClosureRequests.AsNoTracking().Single(r => r.Id == id).RowVersion);
    }
}
