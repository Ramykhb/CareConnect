using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Chat;
using Xunit;

namespace PetSitting.Tests;

// #14 chat alerts and unread badge, #20 SignalR served locally.
public sealed class ChatAlertsAndOfflineTests
{
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));

    [Fact]
    public async Task SignalR_client_is_served_by_the_app_itself()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/lib/microsoft-signalr/signalr.min.js");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("signalR", await response.Content.ReadAsStringAsync());
        var layout = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PetSitting", "Views", "Shared", "_PortalLayout.cshtml"));
        Assert.Contains("~/lib/microsoft-signalr/signalr.min.js", layout);
        Assert.DoesNotContain("cdnjs.cloudflare.com", layout);
    }

    [SqlServerFact]
    public async Task A_new_message_alerts_the_other_person_once_until_they_open_the_chat()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        ApplicationUser NewUser(string name) => new() { Id = Guid.NewGuid(), FirstName = name, LastName = "Chat", UserName = "chat-" + Guid.NewGuid().ToString("N") };
        var owner = NewUser("Owner"); var sitter = NewUser("Sitter");
        var start = DateTimeOffset.UtcNow.AddDays(5);
        var profile = new PetSitterProfile { User = sitter, Bio = "Test", City = "Beirut", HourlyRate = 10 };
        var request = new PetCareRequest { Customer = owner, Title = "Chat care", Description = "Test", City = "Beirut", StartsAtUtc = start, EndsAtUtc = start.AddHours(2), Budget = 20, Status = RequestStatus.Booked };
        var offer = new PetCareOffer { Request = request, SitterProfile = profile, Amount = 20, Status = OfferStatus.Accepted };
        var booking = new PetBooking { Request = request, Offer = offer, Customer = owner, SitterProfile = profile, StartsAtUtc = start, EndsAtUtc = start.AddHours(2), TotalAmount = 20, Status = BookingStatus.Confirmed };
        db.AddRange(owner, sitter, profile, request, offer, booking);
        await db.SaveChangesAsync();
        try
        {
            var chat = new ChatService(db, TimeProvider.System);
            Task<List<Notification>> Alerts() => db.Notifications.AsNoTracking().Where(n => n.UserId == sitter.Id && n.SourceKey!.StartsWith("chat:")).ToListAsync();
            Assert.NotNull(await chat.SendAsync(owner.Id, booking.Id, "Hello, is 6pm still fine?"));
            Assert.NotNull(await chat.SendAsync(owner.Id, booking.Id, "Also, the cat needs water."));
            var alert = Assert.Single(await Alerts());                                   // collapsed while unread
            Assert.Equal("New message from Owner", alert.Title);
            Assert.Equal("/Messages/Chat/" + booking.Id, alert.RelativeUrl);
            Assert.Equal(2, await chat.UnreadAsync(sitter.Id));                           // badge counts messages
            Assert.Empty(await db.Notifications.Where(n => n.UserId == owner.Id && n.SourceKey!.StartsWith("chat:")).ToListAsync()); // the sender gets none

            await chat.MarkReadAsync(sitter.Id, booking.Id);
            Assert.All(await Alerts(), n => Assert.NotNull(n.ReadAtUtc));                // opening the chat clears the alert
            Assert.Equal(0, await chat.UnreadAsync(sitter.Id));

            await chat.SendAsync(owner.Id, booking.Id, "One more thing");
            Assert.Equal(2, (await Alerts()).Count);                                      // a fresh alert after reading
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Notifications.Where(n => n.UserId == owner.Id || n.UserId == sitter.Id).ExecuteDeleteAsync();
            await db.ChatMessages.Where(m => m.BookingId == booking.Id).ExecuteDeleteAsync();
            await db.PetBookings.Where(b => b.Id == booking.Id).ExecuteDeleteAsync();
            await db.PetCareOffers.Where(o => o.Id == offer.Id).ExecuteDeleteAsync();
            await db.PetCareRequests.Where(r => r.Id == request.Id).ExecuteDeleteAsync();
            await db.PetSitterProfiles.Where(p => p.Id == profile.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == sitter.Id).ExecuteDeleteAsync();
        }
    }
}
