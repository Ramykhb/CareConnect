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

// Booking chat over sockets; messages are deleted 24 hours after being read.
public sealed class ChatTests
{
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));

    [Fact]
    public async Task Messages_require_sign_in_and_the_chat_socket_rejects_anonymous_connections()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var page = await client.GetAsync("/Messages");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("/Identity/Account/Login", page.Headers.Location!.ToString());
        using var socket = await client.PostAsync(ChatHub.Path + "/negotiate?negotiateVersion=1", null);
        Assert.NotEqual(HttpStatusCode.OK, socket.StatusCode);
    }

    [SqlServerFact]
    public async Task Only_booking_members_chat_and_read_messages_disappear_after_24_hours()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        ApplicationUser User(string name) => new() { Id = Guid.NewGuid(), FirstName = name, LastName = "Chat", UserName = "chat-" + Guid.NewGuid().ToString("N") };
        var owner = User("Owner"); var sitter = User("Sitter"); var stranger = User("Stranger");
        var profile = new PetSitterProfile { User = sitter, Bio = "Test", City = "Beirut", HourlyRate = 10 };
        var start = DateTimeOffset.UtcNow.AddDays(2);
        var request = new PetCareRequest { Customer = owner, Title = "Chat booking", Description = "Test", City = "Beirut", StartsAtUtc = start, EndsAtUtc = start.AddHours(3), Budget = 30, Status = RequestStatus.Booked };
        var offer = new PetCareOffer { Request = request, SitterProfile = profile, Amount = 30, Status = OfferStatus.Accepted };
        var booking = new PetBooking { Request = request, Offer = offer, Customer = owner, SitterProfile = profile, StartsAtUtc = start, EndsAtUtc = start.AddHours(3), TotalAmount = 30, Status = BookingStatus.Confirmed };
        db.AddRange(owner, sitter, stranger, profile, request, offer, booking);
        await db.SaveChangesAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var chat = new ChatService(db, clock);
        try
        {
            Assert.Equal("/OwnerBookings/Details/" + request.Id, (await chat.ThreadAsync(owner.Id, booking.Id))!.BookingUrl);
            Assert.Equal("/SitterBookings/Details/" + booking.Id, (await chat.ThreadAsync(sitter.Id, booking.Id))!.BookingUrl);
            Assert.False(await chat.CanAccessAsync(stranger.Id, booking.Id));
            Assert.Null(await chat.SendAsync(stranger.Id, booking.Id, "Let me in"));
            Assert.Null(await chat.ThreadAsync(stranger.Id, booking.Id));
            Assert.Null(await chat.SendAsync(owner.Id, booking.Id, "   "));                       // empty
            Assert.Null(await chat.SendAsync(owner.Id, booking.Id, new string('x', 2001)));        // too long

            var sent = await chat.SendAsync(owner.Id, booking.Id, "Hi! Bedtime is at 8.");
            Assert.NotNull(sent);
            Assert.Equal(1, await chat.UnreadAsync(sitter.Id));
            Assert.Empty(await chat.MarkReadAsync(stranger.Id, booking.Id));                        // strangers can't mark anything

            var read = Assert.Single(await chat.MarkReadAsync(sitter.Id, booking.Id));
            Assert.Equal(sent!.Id, read.Id);
            Assert.Equal(read.ReadAtUtc!.Value.AddHours(24), read.ExpiresAtUtc);
            await chat.SendAsync(sitter.Id, booking.Id, "Got it, thanks!");                          // unread reply: never expires on its own

            clock.Now = read.ReadAtUtc!.Value.AddHours(23);
            Assert.Equal(2, (await chat.ThreadAsync(owner.Id, booking.Id))!.Messages.Count);
            Assert.Equal(0, await ChatService.DeleteExpiredAsync(db, clock.Now));

            clock.Now = read.ReadAtUtc!.Value.AddHours(24).AddMinutes(1);
            Assert.Single((await chat.ThreadAsync(owner.Id, booking.Id))!.Messages);              // hidden even before cleanup
            Assert.Equal(1, await ChatService.DeleteExpiredAsync(db, clock.Now));
            Assert.Equal(1, await db.ChatMessages.CountAsync(m => m.BookingId == booking.Id));     // the unread reply stays
        }
        finally
        {
            db.ChangeTracker.Clear();
            var users = new[] { owner.Id, sitter.Id, stranger.Id };
            await db.ChatMessages.Where(m => m.BookingId == booking.Id).ExecuteDeleteAsync();
            await db.Notifications.Where(n => users.Contains(n.UserId)).ExecuteDeleteAsync();
            await db.PetBookings.Where(b => b.Id == booking.Id).ExecuteDeleteAsync();
            await db.PetCareOffers.Where(o => o.Id == offer.Id).ExecuteDeleteAsync();
            await db.PetCareRequests.Where(r => r.Id == request.Id).ExecuteDeleteAsync();
            await db.PetSitterProfiles.Where(p => p.Id == profile.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => users.Contains(u.Id)).ExecuteDeleteAsync();
        }
    }
}
