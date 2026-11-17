using System.Security.Claims;
using System.Text.Json;
using CareConnect.Contracts.Messaging;
using CareConnect.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PetDb = PetSitting.Data.PetSittingDbContext;
using BabyDb = BabySitting.Data.BabySittingDbContext;
using Pet = PetSitting.Models;
using Baby = BabySitting.Models;
using Xunit;
using RabbitMQ.Client;

namespace CareConnect.Messaging.Tests;

public sealed class MessagingFactAttribute : FactAttribute
{
    public MessagingFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CARECONNECT_MESSAGING_TESTS") != "1")
            Skip = "Opt in with CARECONNECT_MESSAGING_TESTS=1, SQL Server and an isolated RabbitMQ broker.";
    }
}

public sealed class CombinedCareTests
{
    [Theory]
    [InlineData("BabySitting", 2, "State", "Open")]
    [InlineData("PetSitting", 1, "State", "Open")]
    [InlineData("BabySitting", 1, "Unknown", "Open")]
    [InlineData("BabySitting", 1, "State", "999")]
    public void Unsupported_events_are_rejected(string source, int version, string type, string status)
    {
        var message = new CombinedCareEvent(new(Guid.NewGuid(), Guid.NewGuid(), type, version, source, DateTimeOffset.UtcNow),
            Guid.NewGuid(), null, 1, status, null);
        Assert.Throws<InvalidDataException>(() => PetSitting.Services.Messaging.IntegrationStore.Validate(message));
    }

    [MessagingFact]
    public async Task Broker_delivers_links_lifecycle_cancellation_and_deduplicates_replays()
    {
        var petConfig = new ConfigurationBuilder().AddUserSecrets(typeof(PetDb).Assembly).AddEnvironmentVariables().Build();
        var babyConfig = new ConfigurationBuilder().AddUserSecrets(typeof(BabyDb).Assembly).AddEnvironmentVariables().Build();
        var options = new BrokerOptions { Enabled = true, HostName = "localhost",
            Port = int.Parse(Environment.GetEnvironmentVariable("CARECONNECT_RABBITMQ_PORT") ?? "5672"),
            UserName = Environment.GetEnvironmentVariable("CARECONNECT_RABBITMQ_USER") ?? "careconnect",
            Password = Environment.GetEnvironmentVariable("CARECONNECT_RABBITMQ_PASSWORD") ?? throw new InvalidOperationException("Set broker password.") };
        using var petHost = CreateHost<PetDb, PetSitting.Services.Messaging.IntegrationStore, Pet.ApplicationUser>(petConfig, options, "PetSitting");
        using var babyHost = CreateHost<BabyDb, BabySitting.Services.Messaging.IntegrationStore, Baby.ApplicationUser>(babyConfig, options, "BabySitting");
        var petOwner = new Pet.ApplicationUser { Id = Guid.NewGuid(), FirstName = "Combined", LastName = "Test", UserName = Guid.NewGuid().ToString() };
        var babyOwner = new Baby.ApplicationUser { Id = Guid.NewGuid(), FirstName = "Combined", LastName = "Test", UserName = Guid.NewGuid().ToString() };
        var sitter = new Baby.ApplicationUser { Id = Guid.NewGuid(), FirstName = "Sitter", LastName = "Test", UserName = Guid.NewGuid().ToString() };
        var start = DateTimeOffset.UtcNow.AddDays(3);
        var petRequest = new Pet.PetCareRequest { CustomerId = petOwner.Id, Title = "Combined test pet", Description = "Test", City = "Integration", StartsAtUtc = start, EndsAtUtc = start.AddHours(2) };
        var babyRequest = new Baby.ChildcareRequest { CustomerId = babyOwner.Id, Title = "Combined test baby", Description = "Test", City = "Integration", StartsAtUtc = start, EndsAtUtc = start.AddHours(2) };
        Guid correlation = Guid.Empty;
        Guid babyBookingId = Guid.Empty;
        try
        {
            using (var scope = petHost.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PetDb>();
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.False(db.Database.HasPendingModelChanges());
                db.AddRange(petOwner, petRequest); await db.SaveChangesAsync();
                var controller = new PetSitting.Controllers.CombinedCareController(db, scope.ServiceProvider.GetRequiredService<UserManager<Pet.ApplicationUser>>(),
                    Options.Create(options), scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>());
                SetUser(controller, petOwner.Id);
                var result = Assert.IsType<ViewResult>(await controller.Invite(new() { RequestId = petRequest.Id }, default));
                var view = Assert.IsType<PetSitting.ViewModels.CombinedCareViewModel>(result.Model);
                var code = Assert.IsType<string>(view.NewCode);
                correlation = Guid.ParseExact(code[..32], "N");
                Assert.Single(await db.MessageOutboxEntries.Where(m => m.CorrelationId == correlation && m.PublishedAtUtc == null).ToListAsync());
                // A rolled-back lifecycle write must leave neither a changed request nor an outgoing event.
                await db.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
                    await using var tx = await db.Database.BeginTransactionAsync();
                    var rollbackRequest = await db.PetCareRequests.SingleAsync(r => r.Id == petRequest.Id);
                    rollbackRequest.Status = Pet.RequestStatus.Cancelled;
                    await db.SaveChangesAsync();
                    Assert.Equal(2, await db.MessageOutboxEntries.CountAsync(m => m.CorrelationId == correlation));
                    await tx.RollbackAsync();
                });
                db.ChangeTracker.Clear();
                Assert.Equal(Pet.RequestStatus.Open, (await db.PetCareRequests.SingleAsync(r => r.Id == petRequest.Id)).Status);
                Assert.Equal(1, await db.MessageOutboxEntries.CountAsync(m => m.CorrelationId == correlation));

                using var babyScope = babyHost.Services.CreateScope();
                var babyDb = babyScope.ServiceProvider.GetRequiredService<BabyDb>();
                Assert.Empty(await babyDb.Database.GetPendingMigrationsAsync());
                Assert.False(babyDb.Database.HasPendingModelChanges());
                babyDb.AddRange(babyOwner, sitter, babyRequest); await babyDb.SaveChangesAsync();
                await petHost.StartAsync(); await babyHost.StartAsync();
                await WaitAsync(async () => await babyDb.CombinedCareLinks.AsNoTracking().AnyAsync(l => l.Id == correlation));
                var join = new BabySitting.Controllers.CombinedCareController(babyDb,
                    babyScope.ServiceProvider.GetRequiredService<UserManager<Baby.ApplicationUser>>(), Options.Create(options),
                    babyScope.ServiceProvider.GetRequiredService<IDataProtectionProvider>());
                SetUser(join, babyOwner.Id);
                Assert.IsType<RedirectToActionResult>(await join.Join(new() { RequestId = babyRequest.Id, Code = code }, default));
            }
            await WaitAsync(async () => {
                using var scope = petHost.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PetDb>();
                return await db.CombinedCareLinks.AnyAsync(l => l.Id == correlation && l.PeerRequestId == babyRequest.Id);
            });
            using (var scope = babyHost.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BabyDb>();
                var request = await db.ChildcareRequests.SingleAsync(r => r.Id == babyRequest.Id);
                var profile = new Baby.BabysitterProfile { UserId = sitter.Id, City = "Integration" };
                var offer = new Baby.BabysittingOffer { RequestId = request.Id, SitterProfile = profile, Status = Baby.OfferStatus.Accepted, Amount = 20 };
                var booking = new Baby.BabysittingBooking { RequestId = request.Id, Offer = offer, SitterProfile = profile, CustomerId = babyOwner.Id,
                    StartsAtUtc = start, EndsAtUtc = start.AddHours(2), Status = Baby.BookingStatus.Confirmed, TotalAmount = 20 };
                babyBookingId = booking.Id;
                db.Add(booking);
                db.Payments.Add(new Baby.Payment { Booking = booking, PayerId = babyOwner.Id, Amount = 20, Provider = "Dummy", IdempotencyKey = Guid.NewGuid(), Status = Baby.PaymentStatus.Succeeded });
                request.Status = Baby.RequestStatus.Booked;
                await db.SaveChangesAsync();
            }
            await WaitAsync(async () => {
                using var scope = petHost.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PetDb>();
                return await db.CombinedCareLinks.AnyAsync(l => l.Id == correlation && l.PeerBookingStatus == "Confirmed");
            });
            using (var scope = petHost.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PetDb>();
                var request = await db.PetCareRequests.SingleAsync(r => r.Id == petRequest.Id);
                var result = await new PetSitting.Services.Bookings.OwnerBookingService(db, TimeProvider.System)
                    .CancelAsync(petOwner.Id, request.Id, Convert.ToBase64String(request.RowVersion), "Cancel both services");
                Assert.True(result.Succeeded);
            }
            await WaitAsync(async () => {
                using var scope = babyHost.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BabyDb>();
                return await db.BabysittingBookings.AnyAsync(b => b.Id == babyBookingId && b.Status == Baby.BookingStatus.Cancelled);
            });
            using (var scope = babyHost.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BabyDb>();
                Assert.Equal(Baby.PaymentStatus.Refunded, (await db.Payments.SingleAsync(p => p.BookingId == babyBookingId)).Status);
                var store = scope.ServiceProvider.GetRequiredService<IIntegrationStore>();
                using var petScope = petHost.Services.CreateScope(); var petDb = petScope.ServiceProvider.GetRequiredService<PetDb>();
                var original = JsonSerializer.Deserialize<CombinedCareEvent>((await petDb.MessageOutboxEntries.Where(m => m.CorrelationId == correlation && m.EventType == "Invited").SingleAsync()).PayloadJson)!;
                await store.ReceiveAsync(original, default); // exact duplicate
                await store.ReceiveAsync(original with { Metadata = original.Metadata with { EventId = Guid.NewGuid() } }, default); // stale revision
                Assert.Equal(Baby.RequestStatus.Cancelled, (await db.ChildcareRequests.AsNoTracking().SingleAsync(r => r.Id == babyRequest.Id)).Status);
                Assert.Equal(1, await db.ProcessedMessages.CountAsync(m => m.EventId == original.Metadata.EventId));
            }
            // Exercise broker retry and poison-message routing without waiting through all retry delays.
            var factory = new ConnectionFactory { HostName = options.HostName, Port = options.Port, UserName = options.UserName, Password = options.Password };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
            var poisonId = Guid.NewGuid(); var transientId = Guid.NewGuid();
            foreach (var (id, version) in new[] { (poisonId, 99), (transientId, 1) })
            {
                var message = new CombinedCareEvent(new(id, Guid.NewGuid(), "State", version, "BabySitting", DateTimeOffset.UtcNow), Guid.NewGuid(), null, 1, "Open", null);
                await channel.BasicPublishAsync("", "petsitting.events", true, new BasicProperties { Persistent = true, MessageId = id.ToString(), CorrelationId = message.Metadata.CorrelationId.ToString() }, JsonSerializer.SerializeToUtf8Bytes(message));
            }
            await WaitAsync(async () => {
                var delivery = await channel.BasicGetAsync("petsitting.events.dead", false);
                if (delivery is null) return false;
                Assert.Equal(poisonId.ToString(), delivery.BasicProperties.MessageId);
                await channel.BasicAckAsync(delivery.DeliveryTag, false);
                return true;
            });
            await WaitAsync(async () => {
                var delivery = await channel.BasicGetAsync("petsitting.events.retry", false);
                if (delivery is null) return false;
                Assert.Equal(transientId.ToString(), delivery.BasicProperties.MessageId);
                Assert.Equal(1, Convert.ToInt32(delivery.BasicProperties.Headers!["careconnect-attempt"]));
                await channel.BasicAckAsync(delivery.DeliveryTag, false);
                return true;
            });
        }
        finally
        {
            await petHost.StopAsync(); await babyHost.StopAsync();
            using var pScope = petHost.Services.CreateScope(); var p = pScope.ServiceProvider.GetRequiredService<PetDb>();
            using var bScope = babyHost.Services.CreateScope(); var b = bScope.ServiceProvider.GetRequiredService<BabyDb>();
            await p.Notifications.Where(n => n.UserId == petOwner.Id).ExecuteDeleteAsync();
            await b.Notifications.Where(n => n.UserId == babyOwner.Id || n.UserId == sitter.Id).ExecuteDeleteAsync();
            await p.MessageOutboxEntries.Where(m => m.CorrelationId == correlation).ExecuteDeleteAsync();
            await b.MessageOutboxEntries.Where(m => m.CorrelationId == correlation).ExecuteDeleteAsync();
            await p.ProcessedMessages.Where(m => m.CorrelationId == correlation).ExecuteDeleteAsync();
            await b.ProcessedMessages.Where(m => m.CorrelationId == correlation).ExecuteDeleteAsync();
            await p.CombinedCareLinks.Where(l => l.Id == correlation).ExecuteDeleteAsync();
            await b.CombinedCareLinks.Where(l => l.Id == correlation).ExecuteDeleteAsync();
            await b.Payments.Where(m => m.BookingId == babyBookingId).ExecuteDeleteAsync();
            await b.BabysittingBookings.Where(m => m.RequestId == babyRequest.Id).ExecuteDeleteAsync();
            await b.BabysittingOffers.Where(m => m.RequestId == babyRequest.Id).ExecuteDeleteAsync();
            await b.BabysitterProfiles.Where(m => m.UserId == sitter.Id).ExecuteDeleteAsync();
            await p.PetCareRequests.Where(r => r.Id == petRequest.Id).ExecuteDeleteAsync();
            await b.ChildcareRequests.Where(r => r.Id == babyRequest.Id).ExecuteDeleteAsync();
            await p.Users.Where(u => u.Id == petOwner.Id).ExecuteDeleteAsync();
            await b.Users.Where(u => u.Id == babyOwner.Id || u.Id == sitter.Id).ExecuteDeleteAsync();
        }
    }

    private static IHost CreateHost<TDb, TStore, TUser>(IConfiguration config, BrokerOptions template, string platform)
        where TDb : DbContext where TStore : class, IIntegrationStore where TUser : class
        => Host.CreateDefaultBuilder().ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning)).ConfigureServices(s => {
            s.AddDbContext<TDb>(o => o.UseSqlServer(config.GetConnectionString("DefaultConnection"), sql => sql.EnableRetryOnFailure()));
            s.AddIdentityCore<TUser>(o => o.Stores.MaxLengthForKeys = 128).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<TDb>();
            s.AddDataProtection();
            s.Configure<BrokerOptions>(o => { o.Enabled = true; o.HostName = template.HostName; o.Port = template.Port;
                o.UserName = template.UserName; o.Password = template.Password; o.Platform = platform; o.Queue = platform.ToLowerInvariant() + ".events"; });
            s.AddSingleton<BrokerHealth>(); s.AddScoped<IIntegrationStore, TStore>(); s.AddHostedService<RabbitMqWorker>();
        }).Build();

    private static void SetUser(Controller controller, Guid userId)
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test")) };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new TestTempData());
    }
    private sealed class TestTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (DateTimeOffset.UtcNow < deadline) { if (await condition()) return; await Task.Delay(200); }
        Assert.Fail("The broker did not deliver the expected state within 25 seconds.");
    }
}
