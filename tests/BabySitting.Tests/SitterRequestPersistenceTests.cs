using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Requests;
using BabySitting.ViewModels;
using Xunit;

namespace BabySitting.Tests;

public sealed class SitterRequestPersistenceTests
{
    internal sealed class Scenario : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; } = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        public ApplicationUser Owner { get; } = new() { FirstName = "Sarah", LastName = "RequestTest" };
        public ApplicationUser Sitter { get; } = new() { FirstName = "Omar", LastName = "RequestTest" };
        public ApplicationUser OtherSitter { get; } = new() { FirstName = "Karim", LastName = "RequestTest" };
        public List<ChildcareRequest> Requests { get; } = [];
        public Guid ProfileId { get; private set; }
        public static async Task<Scenario> CreateAsync()
        {
            var scenario = new Scenario();
            try
            {
                using var scope = scenario.Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
                var city = "request-test-" + Guid.NewGuid().ToString("N");
                var now = DateTimeOffset.UtcNow;
                scenario.Owner.City = scenario.Sitter.City = scenario.OtherSitter.City = city;
                db.Users.AddRange(scenario.Owner, scenario.Sitter, scenario.OtherSitter);
                var profile = new BabysitterProfile { User = scenario.Sitter, Bio = "Test sitter", City = city, HourlyRate = 5, Services = [new() { ServiceType = ServiceType.Babysitting }], Availability = [new() { StartsAtUtc = now, EndsAtUtc = now.AddDays(15) }] };
                scenario.ProfileId = profile.Id;
                db.BabysitterProfiles.AddRange(profile, new() { User = scenario.OtherSitter, Bio = "Other test sitter", City = city, HourlyRate = 5 });
                var family = new FamilyProfile { User = scenario.Owner, City = city, NumberOfChildren = 2, YoungestChildAgeYears = 3, OldestChildAgeYears = 7, HealthNotes = "Peanut allergy", Routines = "Bath at 7, bed at 8" };
                db.FamilyProfiles.Add(family);
                for (var i = 0; i < 4; i++)
                {
                    var request = new ChildcareRequest { Customer = scenario.Owner, Title = "Request test " + i, Description = "Please follow the bedtime routine.", NumberOfChildren = 2, YoungestChildAgeYears = 3, OldestChildAgeYears = 7, CareInstructions = "Story before bed", City = city,
                        ServiceType = ServiceType.Babysitting, Budget = 135 + i, StartsAtUtc = now.AddDays(i + 1), EndsAtUtc = now.AddDays(i + 1).AddHours(2),
                        ServiceAddress = "Private address for accepted sitter only", FamilyProfile = family };
                    scenario.Requests.Add(request);
                }
                db.ChildcareRequests.AddRange(scenario.Requests);
                await db.SaveChangesAsync();
                return scenario;
            }
            catch { await scenario.DisposeAsync(); throw; }
        }
        public async Task<SitterRequestsViewModel> ReadAsync(ApplicationUser? actor = null)
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ISitterRequestService>().LoadAsync(actor ?? Sitter);
        }
        public async Task<RequestDecisionForm> FormAsync(int index = 0, string decision = "accept", ApplicationUser? actor = null)
        {
            var id = Requests[index].Id;
            var item = (await ReadAsync(actor)).Items.Single(i => i.Id == id);
            return new() { RequestId = id, Decision = decision, Version = item.Version };
        }
        public async Task<RequestDecisionResult> DecideAsync(RequestDecisionForm form, ApplicationUser? actor = null)
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ISitterRequestService>().DecideAsync(actor ?? Sitter, form);
        }
        public async Task<T> QueryAsync<T>(Func<BabySittingDbContext, Task<T>> query)
        {
            using var scope = Factory.Services.CreateScope();
            return await query(scope.ServiceProvider.GetRequiredService<BabySittingDbContext>());
        }
        public async Task ChangeAsync(Func<BabySittingDbContext, Task> change)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
            await change(db);
            await db.SaveChangesAsync();
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                using var scope = Factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
                var users = new[] { Owner.Id, Sitter.Id, OtherSitter.Id };
                var requestIds = Requests.Select(r => r.Id).ToArray();
                await db.Notifications.Where(n => users.Contains(n.UserId)).ExecuteDeleteAsync();
                await db.BookingUpdatePhotos.Where(p => requestIds.Contains(p.BookingUpdate.Booking.RequestId)).ExecuteDeleteAsync();
                await db.BookingUpdates.Where(u => requestIds.Contains(u.Booking.RequestId)).ExecuteDeleteAsync();
                await db.BookingCareTasks.Where(t => requestIds.Contains(t.Booking.RequestId)).ExecuteDeleteAsync();
                await db.Payments.Where(p => requestIds.Contains(p.Booking.RequestId)).ExecuteDeleteAsync();
                await db.BabysittingBookings.Where(b => requestIds.Contains(b.RequestId)).ExecuteDeleteAsync();
                await db.BabysittingOffers.Where(o => requestIds.Contains(o.RequestId)).ExecuteDeleteAsync();
                await db.ChildcareRequests.Where(r => requestIds.Contains(r.Id)).ExecuteDeleteAsync();
                await db.FamilyProfiles.Where(f => f.UserId == Owner.Id).ExecuteDeleteAsync();
                await db.SitterScheduleExceptions.Where(e => users.Contains(e.SitterProfile.UserId)).ExecuteDeleteAsync();
                await db.SitterAvailabilities.Where(a => users.Contains(a.SitterProfile.UserId)).ExecuteDeleteAsync();
                await db.SitterServices.Where(s => users.Contains(s.SitterProfile.UserId)).ExecuteDeleteAsync();
                await db.BabysitterProfiles.Where(p => users.Contains(p.UserId)).ExecuteDeleteAsync();
                await db.Users.Where(u => users.Contains(u.Id)).ExecuteDeleteAsync();
            }
            finally { await Factory.DisposeAsync(); }
        }
    }

    [SqlServerFact]
    public async Task Acceptance_saves_booking_notification_and_address_access_once()
    {
        await using var s = await Scenario.CreateAsync();
        var before = (await s.ReadAsync()).Items.Single(i => i.Id == s.Requests[0].Id);
        Assert.Null(before.Address);
        Assert.True(before.CanAccept);
        Assert.Equal("Peanut allergy", before.Children.HealthNotes);
        var form = await s.FormAsync();
        var result = await s.DecideAsync(form);
        Assert.True(result.Succeeded, result.Message);
        var accepted = (await s.ReadAsync()).Items.Single(i => i.Id == form.RequestId);
        Assert.Equal(IncomingRequestState.Accepted, accepted.State);
        Assert.Equal(BookingStatus.AwaitingPayment, accepted.BookingStatus);
        Assert.Equal("Private address for accepted sitter only", accepted.Address);
        var booking = await s.QueryAsync(db => db.BabysittingBookings.SingleAsync(b => b.Id == result.BookingId));
        Assert.Equal(s.Owner.Id, booking.CustomerId);
        Assert.Equal(s.ProfileId, booking.SitterProfileId);
        Assert.Equal(135m, booking.TotalAmount);
        var again = await s.DecideAsync(form);
        Assert.True(again.Succeeded);
        Assert.Equal(result.BookingId, again.BookingId);
        Assert.Equal(1, await s.QueryAsync(db => db.BabysittingBookings.CountAsync(b => b.RequestId == form.RequestId)));
        Assert.Equal(1, await s.QueryAsync(db => db.BabysittingOffers.CountAsync(o => o.RequestId == form.RequestId)));
        Assert.Equal(1, await s.QueryAsync(db => db.Notifications.CountAsync(n => n.UserId == s.Owner.Id)));
        form.Decision = "decline";
        Assert.False((await s.DecideAsync(form)).Succeeded);
    }

    [SqlServerFact]
    public async Task Profile_setup_is_required_and_zero_cost_care_does_not_await_payment()
    {
        await using var s = await Scenario.CreateAsync();
        var unconfigured = new ApplicationUser { FirstName = "New sitter", City = s.Sitter.City };
        var inbox = await s.ReadAsync(unconfigured);
        Assert.NotEmpty(inbox.Items);
        Assert.All(inbox.Items, item => { Assert.False(item.CanAccept); Assert.False(item.CanDecline); });
        var version = inbox.Items.Single(i => i.Id == s.Requests[0].Id).Version;
        Assert.False((await s.DecideAsync(new() { RequestId = s.Requests[0].Id, Decision = "accept", Version = version }, unconfigured)).Succeeded);
        await s.ChangeAsync(async db => (await db.ChildcareRequests.SingleAsync(r => r.Id == s.Requests[0].Id)).Budget = 0);
        Assert.True((await s.DecideAsync(await s.FormAsync())).Succeeded);
        Assert.Equal(BookingStatus.Confirmed, await s.QueryAsync(db => db.BabysittingBookings.Where(b => b.RequestId == s.Requests[0].Id).Select(b => b.Status).SingleAsync()));
    }

    [SqlServerFact]
    public async Task Decline_is_private_to_the_sitter_and_another_sitter_can_accept()
    {
        await using var s = await Scenario.CreateAsync();
        var form = await s.FormAsync(decision: "decline");
        Assert.True((await s.DecideAsync(form)).Succeeded);
        Assert.True((await s.DecideAsync(form)).Succeeded);
        Assert.Equal(RequestStatus.Open, await s.QueryAsync(db => db.ChildcareRequests.Where(r => r.Id == form.RequestId).Select(r => r.Status).SingleAsync()));
        Assert.Equal(0, await s.QueryAsync(db => db.BabysittingBookings.CountAsync(b => b.RequestId == form.RequestId)));
        Assert.Equal(1, await s.QueryAsync(db => db.Notifications.CountAsync(n => n.UserId == s.Owner.Id)));
        var otherForm = await s.FormAsync(actor: s.OtherSitter);
        Assert.True((await s.DecideAsync(otherForm, s.OtherSitter)).Succeeded);
        var declined = (await s.ReadAsync()).Items.Single(i => i.Id == form.RequestId);
        Assert.Equal(IncomingRequestState.Declined, declined.State);
        Assert.Null(declined.Address);
        Assert.Equal(2, await s.QueryAsync(db => db.Notifications.CountAsync(n => n.UserId == s.Owner.Id)));
    }

    [SqlServerFact]
    public async Task Updated_requests_and_quotes_require_a_fresh_confirmation()
    {
        await using var s = await Scenario.CreateAsync();
        var form = await s.FormAsync();
        await s.ChangeAsync(async db => (await db.ChildcareRequests.SingleAsync(r => r.Id == form.RequestId)).Budget = 180);
        Assert.False((await s.DecideAsync(form)).Succeeded);
        Assert.Equal(0, await s.QueryAsync(db => db.BabysittingBookings.CountAsync(b => b.RequestId == form.RequestId)));
        await s.ChangeAsync(db => { db.BabysittingOffers.Add(new() { RequestId = form.RequestId, SitterProfileId = s.ProfileId, Amount = 160 }); return Task.CompletedTask; });
        var quoted = await s.FormAsync();
        await s.ChangeAsync(async db => (await db.BabysittingOffers.SingleAsync(o => o.RequestId == form.RequestId)).Amount = 170);
        Assert.False((await s.DecideAsync(quoted)).Succeeded);
        Assert.Equal(0, await s.QueryAsync(db => db.Notifications.CountAsync(n => n.UserId == s.Owner.Id)));
        var fresh = await s.FormAsync();
        Assert.True((await s.DecideAsync(fresh)).Succeeded);
        Assert.Equal(170m, await s.QueryAsync(db => db.BabysittingBookings.Where(b => b.RequestId == form.RequestId).Select(b => b.TotalAmount).SingleAsync()));
    }

    [SqlServerFact]
    public async Task Decisions_enforce_conflicts_availability_expiration_and_service_scope()
    {
        await using var s = await Scenario.CreateAsync();
        Assert.True((await s.DecideAsync(await s.FormAsync())).Succeeded);
        await s.ChangeAsync(async db => { var second = await db.ChildcareRequests.SingleAsync(r => r.Id == s.Requests[1].Id); second.StartsAtUtc = s.Requests[0].StartsAtUtc; second.EndsAtUtc = s.Requests[0].EndsAtUtc; });
        var overlap = await s.FormAsync(1);
        Assert.Contains("overlap", (await s.DecideAsync(overlap)).Message);
        await s.ChangeAsync(async db => (await db.BabysitterProfiles.SingleAsync(p => p.Id == s.ProfileId)).IsAvailable = false);
        Assert.False((await s.DecideAsync(await s.FormAsync(2))).Succeeded);
        await s.ChangeAsync(async db => { (await db.BabysitterProfiles.SingleAsync(p => p.Id == s.ProfileId)).IsAvailable = true; var slot = await db.SitterAvailabilities.SingleAsync(a => a.SitterProfileId == s.ProfileId); slot.EndsAtUtc = DateTimeOffset.UtcNow.AddHours(1); });
        Assert.Contains("availability", (await s.DecideAsync(await s.FormAsync(2))).Message);
        var outside = await s.FormAsync(3);
        await s.ChangeAsync(async db => (await db.ChildcareRequests.SingleAsync(r => r.Id == outside.RequestId)).City = "Different service city");
        Assert.True((await s.DecideAsync(outside)).NotFound);
        var expired = await s.FormAsync(2);
        await s.ChangeAsync(async db => { var request = await db.ChildcareRequests.SingleAsync(r => r.Id == expired.RequestId); request.StartsAtUtc = DateTimeOffset.UtcNow.AddDays(-2); request.EndsAtUtc = DateTimeOffset.UtcNow.AddDays(-1); });
        Assert.False((await s.DecideAsync(expired)).Succeeded);
        Assert.Equal(1, await s.QueryAsync(db => db.BabysittingBookings.CountAsync(b => s.Requests.Select(r => r.Id).Contains(b.RequestId))));
        Assert.Equal(1, await s.QueryAsync(db => db.Notifications.CountAsync(n => n.UserId == s.Owner.Id)));
    }

    [SqlServerFact]
    public async Task Concurrent_acceptance_by_two_sitters_creates_one_booking()
    {
        await using var s = await Scenario.CreateAsync();
        var first = await s.FormAsync();
        var second = await s.FormAsync(actor: s.OtherSitter);
        var results = await Task.WhenAll(s.DecideAsync(first), s.DecideAsync(second, s.OtherSitter));
        Assert.Single(results, r => r.Succeeded);
        Assert.Equal(1, await s.QueryAsync(db => db.BabysittingBookings.CountAsync(b => b.RequestId == first.RequestId)));
        Assert.Equal(1, await s.QueryAsync(db => db.Notifications.CountAsync(n => n.UserId == s.Owner.Id)));
    }

    [SqlServerFact]
    public async Task Concurrent_overlapping_requests_cannot_double_book_a_sitter()
    {
        await using var s = await Scenario.CreateAsync();
        await s.ChangeAsync(async db => { var second = await db.ChildcareRequests.SingleAsync(r => r.Id == s.Requests[1].Id); second.StartsAtUtc = s.Requests[0].StartsAtUtc; second.EndsAtUtc = s.Requests[0].EndsAtUtc; });
        var first = await s.FormAsync();
        var second = await s.FormAsync(1);
        var results = await Task.WhenAll(s.DecideAsync(first), s.DecideAsync(second));
        Assert.Single(results, r => r.Succeeded);
        Assert.Equal(1, await s.QueryAsync(db => db.BabysittingBookings.CountAsync(b => b.SitterProfileId == s.ProfileId)));
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Simulated failure before transaction commit.");
    }

    [SqlServerFact]
    public async Task A_failure_after_writes_rolls_back_the_entire_decision()
    {
        await using var s = await Scenario.CreateAsync();
        var form = await s.FormAsync();
        using var scope = s.Factory.Services.CreateScope();
        var existing = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var options = new DbContextOptionsBuilder<BabySittingDbContext>().UseSqlServer(existing.Database.GetConnectionString(), sql => sql.EnableRetryOnFailure()).AddInterceptors(new FailAfterSave()).Options;
        await using var db = new BabySittingDbContext(options);
        var service = new SitterRequestService(db, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync(s.Sitter, form));
        Assert.Equal(RequestStatus.Open, await s.QueryAsync(context => context.ChildcareRequests.Where(r => r.Id == form.RequestId).Select(r => r.Status).SingleAsync()));
        Assert.Equal(0, await s.QueryAsync(context => context.BabysittingBookings.CountAsync(b => b.RequestId == form.RequestId)));
        Assert.Equal(0, await s.QueryAsync(context => context.BabysittingOffers.CountAsync(o => o.RequestId == form.RequestId)));
        Assert.Equal(0, await s.QueryAsync(context => context.Notifications.CountAsync(n => n.UserId == s.Owner.Id)));
    }
}
