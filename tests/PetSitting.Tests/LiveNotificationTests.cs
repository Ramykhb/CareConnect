using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Notifications;
using Xunit;

namespace PetSitting.Tests;

// WatchWise-style live notifications: saved notifications are pushed to their owner, and feed changes announce themselves.
public sealed class LiveNotificationTests
{
    // Records what would be sent over SignalR instead of opening a socket.
    private sealed class FakeHub : IHubContext<NotificationHub>, IHubClients, IClientProxy
    {
        public List<(string User, string Event, object? Payload)> Sent { get; } = [];
        private string user = "";
        public IHubClients Clients => this;
        public IGroupManager Groups => throw new NotSupportedException();
        public IClientProxy User(string userId) { user = userId; return this; }
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) { Sent.Add((user, method, args.FirstOrDefault())); return Task.CompletedTask; }
        public IClientProxy All => this;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Client(string connectionId) => this;
        IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> connectionIds) => this;
        public IClientProxy Group(string groupName) => this;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
        IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groupNames) => this;
        public IClientProxy Users(IReadOnlyList<string> userIds) => this;
    }

    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));

    [Theory]
    [InlineData("/Notifications")]
    [InlineData("/Notifications/Count")]
    public async Task Notification_pages_require_sign_in(string path)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Identity/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task The_live_channel_rejects_anonymous_connections()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.PostAsync(NotificationHub.Path + "/negotiate?negotiateVersion=1", null);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [SqlServerFact]
    public async Task Saved_notifications_are_pushed_and_feed_changes_are_announced_to_their_owner_only()
    {
        await using var factory = Factory();
        var connection = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection");
        var hub = new FakeHub();
        var live = new LiveNotifications(hub, NullLogger<LiveNotifications>.Instance);
        var options = new DbContextOptionsBuilder<PetSittingDbContext>().UseSqlServer(connection).AddInterceptors(new NotificationBroadcastInterceptor(live)).Options;
        var owner = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Live", LastName = "Owner", UserName = "live-" + Guid.NewGuid().ToString("N") };
        var other = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Live", LastName = "Other", UserName = "live-" + Guid.NewGuid().ToString("N") };
        var id = Guid.NewGuid();
        await using var db = new PetSittingDbContext(options);
        try
        {
            db.Users.AddRange(owner, other);
            db.Notifications.Add(new Notification { Id = id, UserId = owner.Id, Title = "Sitter accepted", Body = "Evening care", RelativeUrl = "/x", Category = NotificationCategory.Booking });
            await db.SaveChangesAsync();
            var pushed = Assert.Single(hub.Sent);
            Assert.Equal((owner.Id.ToString(), NotificationHub.Pushed), (pushed.User, pushed.Event));

            var feed = new NotificationFeedService(db, live, TimeProvider.System);
            Assert.Equal(1, await feed.UnreadAsync(owner.Id));
            Assert.Null(await feed.MarkReadAsync(other.Id, id, true));          // not theirs
            Assert.False(await feed.DismissAsync(other.Id, id));
            Assert.NotNull(await feed.MarkReadAsync(owner.Id, id, true));
            Assert.Equal(0, await feed.UnreadAsync(owner.Id));
            Assert.Equal((owner.Id.ToString(), NotificationHub.Changed), (hub.Sent[^1].User, hub.Sent[^1].Event));
            Assert.NotNull(await feed.MarkReadAsync(owner.Id, id, false));      // back to unread
            Assert.Equal(1, await feed.MarkAllReadAsync(owner.Id));
            Assert.True(await feed.DismissAsync(owner.Id, id));
            Assert.Empty(await feed.FeedAsync(owner.Id, unreadOnly: false));
            Assert.DoesNotContain(hub.Sent, s => s.User == other.Id.ToString());
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Notifications.Where(n => n.UserId == owner.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == other.Id).ExecuteDeleteAsync();
        }
    }

    // A pet sitter can cancel before care starts: the offer is withdrawn, the request reopens and the owner is told.
    [SqlServerFact]
    public async Task Pet_sitter_cancel_reopens_the_request_and_notifies_the_owner()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        var owner = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Cancel", LastName = "Owner", UserName = "cancel-" + Guid.NewGuid().ToString("N") };
        var sitter = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Cancel", LastName = "Sitter", UserName = "cancel-" + Guid.NewGuid().ToString("N") };
        var profile = new PetSitterProfile { User = sitter, Bio = "Test", City = "Beirut", HourlyRate = 10 };
        var start = DateTimeOffset.UtcNow.AddDays(3);
        var request = new PetCareRequest { Customer = owner, Title = "Weekend care", Description = "Test", City = "Beirut", StartsAtUtc = start, EndsAtUtc = start.AddHours(4), Budget = 40, Status = RequestStatus.Booked };
        var offer = new PetCareOffer { Request = request, SitterProfile = profile, Amount = 40, Status = OfferStatus.Accepted };
        var booking = new PetBooking { Request = request, Offer = offer, Customer = owner, SitterProfile = profile, StartsAtUtc = start, EndsAtUtc = start.AddHours(4), TotalAmount = 40, Status = BookingStatus.Confirmed };
        db.AddRange(owner, sitter, profile, request, offer, booking);
        await db.SaveChangesAsync();
        try
        {
            var service = new PetSitting.Services.Bookings.BookingCancellationService(db, TimeProvider.System);
            Assert.False((await service.CancelAsync(sitter.Id, booking.Id, "", Convert.ToBase64String(booking.RowVersion))).Succeeded); // a reason is required
            Assert.True((await service.CancelAsync(sitter.Id, booking.Id, "I am sick", Convert.ToBase64String(booking.RowVersion))).Succeeded);
            db.ChangeTracker.Clear();
            Assert.Equal(BookingStatus.Cancelled, (await db.PetBookings.SingleAsync(b => b.Id == booking.Id)).Status);
            Assert.Equal(OfferStatus.Withdrawn, (await db.PetCareOffers.SingleAsync(o => o.Id == offer.Id)).Status);
            Assert.Equal(RequestStatus.Open, (await db.PetCareRequests.SingleAsync(r => r.Id == request.Id)).Status);
            Assert.Equal("/OwnerBookings/Details/" + request.Id, (await db.Notifications.SingleAsync(n => n.UserId == owner.Id)).RelativeUrl);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Notifications.Where(n => n.UserId == owner.Id || n.UserId == sitter.Id).ExecuteDeleteAsync();
            await db.PetBookings.Where(b => b.Id == booking.Id).ExecuteDeleteAsync();
            await db.PetCareOffers.Where(o => o.Id == offer.Id).ExecuteDeleteAsync();
            await db.PetCareRequests.Where(r => r.Id == request.Id).ExecuteDeleteAsync();
            await db.PetSitterProfiles.Where(p => p.UserId == sitter.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == sitter.Id).ExecuteDeleteAsync();
        }
    }
}
