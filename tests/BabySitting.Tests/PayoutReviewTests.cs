using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Portal;
using Xunit;

namespace BabySitting.Tests;

// #5: platform managers mark payout requests as paid.
public sealed class PayoutReviewTests
{
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));

    [Fact]
    public async Task Payouts_page_is_for_signed_in_managers_only()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/Payouts");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Identity/Account/Login", response.Headers.Location!.ToString());
    }

    [SqlServerFact]
    public async Task A_requested_payout_can_be_marked_paid_once_and_the_sitter_is_told()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var sitter = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Payout", LastName = "Sitter", UserName = "payout-" + Guid.NewGuid().ToString("N") };
        var profile = new BabysitterProfile { User = sitter, Bio = "Test", City = "Beirut", HourlyRate = 10 };
        var requested = new SitterPayout { SitterProfile = profile, ClientToken = Guid.NewGuid(), Amount = 25, Currency = "USD", Status = PayoutStatus.Requested };
        var cancelled = new SitterPayout { SitterProfile = profile, ClientToken = Guid.NewGuid(), Amount = 10, Currency = "USD", Status = PayoutStatus.Cancelled };
        db.AddRange(sitter, profile, requested, cancelled);
        await db.SaveChangesAsync();
        try
        {
            var service = new PayoutReviewService(db, TimeProvider.System);
            string Version(Guid id) => Convert.ToBase64String(db.SitterPayouts.AsNoTracking().Single(p => p.Id == id).RowVersion);
            Assert.Contains(await service.ListAsync(PayoutStatus.Requested), i => i.Payout.Id == requested.Id);
            Assert.False((await service.MarkPaidAsync(requested.Id, Convert.ToBase64String(new byte[8]))).Succeeded); // stale page
            Assert.False((await service.MarkPaidAsync(cancelled.Id, Version(cancelled.Id))).Succeeded);               // cancelled by the sitter
            Assert.True((await service.MarkPaidAsync(Guid.NewGuid(), "x")).NotFound);
            Assert.True((await service.MarkPaidAsync(requested.Id, Version(requested.Id))).Succeeded);
            db.ChangeTracker.Clear();
            var paid = await db.SitterPayouts.AsNoTracking().SingleAsync(p => p.Id == requested.Id);
            Assert.Equal(PayoutStatus.Paid, paid.Status);
            Assert.NotNull(paid.PaidAtUtc);
            Assert.Equal("This payout is already marked as paid.", (await service.MarkPaidAsync(requested.Id, Version(requested.Id))).Message);
            var note = Assert.Single(await db.Notifications.AsNoTracking().Where(n => n.UserId == sitter.Id).ToListAsync());
            Assert.StartsWith("Payout sent", note.Title);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Notifications.Where(n => n.UserId == sitter.Id).ExecuteDeleteAsync();
            await db.SitterPayouts.Where(p => p.SitterProfileId == profile.Id).ExecuteDeleteAsync();
            await db.BabysitterProfiles.Where(p => p.Id == profile.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == sitter.Id).ExecuteDeleteAsync();
        }
    }
}
