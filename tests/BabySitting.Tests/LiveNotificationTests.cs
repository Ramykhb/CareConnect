using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Notifications;
using Xunit;

namespace BabySitting.Tests;

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
        var live = new LiveNotifications(hub, NullLogger<LiveNotifications>.Instance, factory.Services.GetRequiredService<IServiceScopeFactory>());
        var options = new DbContextOptionsBuilder<BabySittingDbContext>().UseSqlServer(connection).AddInterceptors(new NotificationBroadcastInterceptor(live)).Options;
        var owner = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Live", LastName = "Owner", UserName = "live-" + Guid.NewGuid().ToString("N") };
        var other = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Live", LastName = "Other", UserName = "live-" + Guid.NewGuid().ToString("N") };
        var id = Guid.NewGuid();
        await using var db = new BabySittingDbContext(options);
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

            var sentBefore = hub.Sent.Count;
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                db.Notifications.Add(new() { UserId = owner.Id, Title = "Rolled back", Body = "Must not be pushed" });
                await db.SaveChangesAsync();
                Assert.Equal(sentBefore, hub.Sent.Count);
                await tx.RollbackAsync();
            }
            db.ChangeTracker.Clear();
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                db.Notifications.Add(new() { UserId = owner.Id, Category = NotificationCategory.Booking, Title = "Committed", Body = "Push only after commit" });
                await db.SaveChangesAsync();
                db.Notifications.Add(new() { UserId = owner.Id, Category = NotificationCategory.Booking, Title = "Second save", Body = "Same transaction" });
                await db.SaveChangesAsync();
                Assert.Equal(sentBefore, hub.Sent.Count);
                await tx.CommitAsync();
            }
            Assert.Equal(sentBefore + 2, hub.Sent.Count);
            Assert.Equal(2, await db.Notifications.CountAsync(n => n.UserId == owner.Id));

            hub.Sent.Clear();
            db.AlertPreferences.Add(new() { UserId = owner.Id, Requests = false, Bookings = false, Payments = false, Reviews = false });
            await db.SaveChangesAsync();
            foreach (var category in new[] { NotificationCategory.Request, NotificationCategory.Booking, NotificationCategory.Payment, NotificationCategory.Review })
                db.Notifications.Add(new() { UserId = owner.Id, Category = category, Title = "Disabled alert", Body = "Retained but suppressed" });
            db.Notifications.Add(new() { UserId = owner.Id, Category = NotificationCategory.General, Title = "Account notice", Body = "General notifications remain visible" });
            await db.SaveChangesAsync();
            Assert.Single(hub.Sent); // only the general notice is delivered live
            Assert.Single(await feed.FeedAsync(owner.Id, false));
            Assert.Equal(1, await feed.UnreadAsync(owner.Id));
            Assert.Equal(new[] { "general" }, await feed.CategoriesAsync(owner.Id));
            Assert.Equal(7, await db.Notifications.CountAsync(n => n.UserId == owner.Id)); // activity is retained
            await db.AlertPreferences.Where(p => p.UserId == owner.Id).ExecuteDeleteAsync();
            Assert.Equal(7, (await feed.FeedAsync(owner.Id, false)).Count); // defaults enable all categories again
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.AlertPreferences.Where(p => p.UserId == owner.Id).ExecuteDeleteAsync();
            await db.Notifications.Where(n => n.UserId == owner.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == other.Id).ExecuteDeleteAsync();
        }
    }
}
