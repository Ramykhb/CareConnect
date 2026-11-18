using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Requests;
using PetSitting.ViewModels;
using Xunit;

namespace PetSitting.Tests;

// #7: when the customer compares offers, sitters send offers and the customer books one of them.
public sealed class OfferChoiceTests
{
    [SqlServerFact]
    public async Task Sitters_send_offers_and_the_customer_books_the_one_they_choose()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        ApplicationUser NewUser(string name) => new() { Id = Guid.NewGuid(), FirstName = name, LastName = "Offer", UserName = "offer-" + Guid.NewGuid().ToString("N") };
        var customer = NewUser("Customer"); var first = NewUser("Ana"); var second = NewUser("Bilal");
        var start = DateTimeOffset.UtcNow.AddDays(7);
        var service = Enum.GetValues<ServiceType>()[0];
        var city = "Offer-" + Guid.NewGuid().ToString("N")[..8];
        PetSitterProfile Profile(ApplicationUser user)
        {
            var p = new PetSitterProfile { User = user, Bio = "Test", City = city, HourlyRate = 1 };
            p.Services.Add(new SitterService { ServiceType = service });
            p.Availability.Add(new SitterAvailability { StartsAtUtc = start.AddDays(-1), EndsAtUtc = start.AddDays(2) });
            return p;
        }
        var p1 = Profile(first); var p2 = Profile(second);
        var request = new PetCareRequest { Customer = customer, Title = "Offer care", Description = "Test", City = city, ServiceType = service,
            StartsAtUtc = start, EndsAtUtc = start.AddHours(3), Budget = 30, Status = RequestStatus.Open, CustomerChoosesSitter = true };
        db.AddRange(customer, first, second, p1, p2, request);
        await db.SaveChangesAsync();
        try
        {
            var inbox = new SitterRequestService(db, TimeProvider.System);
            async Task<RequestDecisionResult> Accept(ApplicationUser sitter, PetSitterProfile profile)
            {
                db.ChangeTracker.Clear();
                var r = await db.PetCareRequests.AsNoTracking().SingleAsync(x => x.Id == request.Id);
                var o = await db.PetCareOffers.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == request.Id && x.SitterProfileId == profile.Id);
                return await inbox.DecideAsync(sitter, new RequestDecisionForm { RequestId = request.Id, Decision = "accept", Version = SitterRequestPolicy.Version(r, o) });
            }
            var sent = await Accept(first, p1);
            Assert.True(sent.Succeeded, sent.Message);
            Assert.Equal("offersent", sent.Filter);
            Assert.True((await Accept(second, p2)).Succeeded);
            Assert.Equal("Your offer was already sent.", (await Accept(second, p2)).Message);
            Assert.False(await db.PetBookings.AnyAsync(b => b.RequestId == request.Id));                         // nobody is booked yet
            Assert.Equal(RequestStatus.Open, (await db.PetCareRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).Status);

            var choice = new OfferChoiceService(db, TimeProvider.System);
            var offers = await choice.OffersAsync(customer.Id, request.Id);
            Assert.Equal(2, offers.Count);
            Assert.Empty(await choice.OffersAsync(first.Id, request.Id));                                      // only the customer sees them
            var chosen = offers.Single(o => o.SitterProfileId == p2.Id);
            string Version() => Convert.ToBase64String(db.PetCareRequests.AsNoTracking().Single(r => r.Id == request.Id).RowVersion);
            Assert.False((await choice.ChooseAsync(customer.Id, request.Id, chosen.OfferId, Convert.ToBase64String(new byte[8]))).Succeeded); // stale page
            Assert.True((await choice.ChooseAsync(first.Id, request.Id, chosen.OfferId, Version())).NotFound);                             // not their request
            var result = await choice.ChooseAsync(customer.Id, request.Id, chosen.OfferId, Version());
            Assert.True(result.Succeeded, result.Message);

            db.ChangeTracker.Clear();
            var booking = await db.PetBookings.AsNoTracking().SingleAsync(b => b.RequestId == request.Id);
            Assert.Equal(p2.Id, booking.SitterProfileId);
            Assert.Equal(BookingStatus.AwaitingPayment, booking.Status);
            Assert.Equal(RequestStatus.Booked, (await db.PetCareRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).Status);
            var statuses = await db.PetCareOffers.AsNoTracking().Where(o => o.RequestId == request.Id).ToDictionaryAsync(o => o.SitterProfileId, o => o.Status);
            Assert.Equal(OfferStatus.Accepted, statuses[p2.Id]);
            Assert.Equal(OfferStatus.Rejected, statuses[p1.Id]);
            Assert.True(await db.Notifications.AnyAsync(n => n.UserId == first.Id && n.Title == "Another sitter was chosen"));
            Assert.True(await db.Notifications.AnyAsync(n => n.UserId == second.Id && n.Title == "Customer chose your offer"));
            Assert.False((await choice.ChooseAsync(customer.Id, request.Id, offers.Single(o => o.SitterProfileId == p1.Id).OfferId, Version())).Succeeded); // already booked
        }
        finally
        {
            db.ChangeTracker.Clear();
            var users = new[] { customer.Id, first.Id, second.Id };
            await db.Notifications.Where(n => users.Contains(n.UserId)).ExecuteDeleteAsync();
            await db.PetBookings.Where(b => b.RequestId == request.Id).ExecuteDeleteAsync();
            await db.PetCareOffers.Where(o => o.RequestId == request.Id).ExecuteDeleteAsync();
            await db.PetCareRequests.Where(r => r.Id == request.Id).ExecuteDeleteAsync();
            var profiles = new[] { p1.Id, p2.Id };
            await db.SitterServices.Where(s => profiles.Contains(s.SitterProfileId)).ExecuteDeleteAsync();
            await db.SitterAvailabilities.Where(a => profiles.Contains(a.SitterProfileId)).ExecuteDeleteAsync();
            await db.PetSitterProfiles.Where(p => profiles.Contains(p.Id)).ExecuteDeleteAsync();
            await db.Users.Where(u => users.Contains(u.Id)).ExecuteDeleteAsync();
        }
    }
}
