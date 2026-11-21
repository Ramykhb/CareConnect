using CareConnect.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.CareSummaries;
using BabySitting.Services.Scheduling;
using Xunit;

namespace BabySitting.Tests;

public sealed class CareSummaryPersistenceTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero); }
    private sealed class Provider : ICareSummaryClient
    {
        public bool Available => true;
        public int Calls;
        public CareSummaryInput? Last;
        public Task<IReadOnlyList<CareSummarySentence>> SummarizeAsync(CareSummaryInput input, CancellationToken ct)
        { Calls++; Last = input; return Task.FromResult<IReadOnlyList<CareSummarySentence>>([new("Recorded care is available.", [input.Sources[0].Key])]); }
    }
    private static async Task Run(Func<BabySittingDbContext, ApplicationUser, BabysittingBooking, Provider, CareSummaryService, Task> test)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development").ConfigureServices(s => s.RemoveAll<IHostedService>()));
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var from = ScheduleMath.Utc(new DateOnly(2026, 11, 20)); var until = ScheduleMath.Utc(new DateOnly(2026, 11, 21));
            var owner = new ApplicationUser { FirstName = "Summary", LastName = "Customer" };
            var sitter = new ApplicationUser { FirstName = "Private", LastName = "Sitter" };
            var profile = new BabysitterProfile { User = sitter, City = "Beirut" };
            var request = new ChildcareRequest { Customer = owner, Title = "Care summary test", Description = "PRIVATE INSTRUCTIONS", ServiceAddress = "PRIVATE ADDRESS", StartsAtUtc = from.AddDays(-1), EndsAtUtc = until.AddDays(1), City = "Beirut", Status = RequestStatus.Booked, Budget = 100 };
            var offer = new BabysittingOffer { Request = request, SitterProfile = profile, Amount = 100, Status = OfferStatus.Accepted };
            var booking = new BabysittingBooking { Request = request, Offer = offer, Customer = owner, SitterProfile = profile, StartsAtUtc = request.StartsAtUtc, EndsAtUtc = request.EndsAtUtc, TotalAmount = 100, Status = BookingStatus.InProgress };
            db.Add(booking); await db.SaveChangesAsync();
            db.BookingCareTasks.AddRange(
                new BookingCareTask { BookingId = booking.Id, Key = "walk", Label = "Activity", Note = "PRIVATE MEDICAL NOTE", CreatedAtUtc = from.AddDays(-1), DueAtUtc = from.AddHours(2), CompletedAtUtc = from.AddHours(3) },
                new BookingCareTask { BookingId = booking.Id, Key = "late", Label = "Refresh water", CreatedAtUtc = from.AddDays(-1), DueAtUtc = from.AddHours(4), CompletedAtUtc = until.AddHours(1) },
                new BookingCareTask { BookingId = booking.Id, Key = "undated", Label = "Undated task", CreatedAtUtc = from.AddDays(-1) },
                new BookingCareTask { BookingId = booking.Id, Key = "next-day", Label = "Next day task", CreatedAtUtc = from.AddDays(-1), DueAtUtc = until });
            db.BookingUpdates.AddRange(
                new BookingUpdate { BookingId = booking.Id, ClientToken = Guid.NewGuid(), Message = "First daily update", CreatedAtUtc = from },
                new BookingUpdate { BookingId = booking.Id, ClientToken = Guid.NewGuid(), Message = "Concern reported", IsIssue = true, CreatedAtUtc = until.AddTicks(-1) },
                new BookingUpdate { BookingId = booking.Id, ClientToken = Guid.NewGuid(), Message = "Next day update", CreatedAtUtc = until },
                new BookingUpdate { BookingId = booking.Id, ClientToken = Guid.NewGuid(), Message = "", HasPhoto = true, CreatedAtUtc = from.AddHours(6) });
            await db.SaveChangesAsync();
            using var cache = new CareSummaryCache(); var provider = new Provider();
            var service = new CareSummaryService(db, new CareSummaryEngine(provider, cache), new Clock());
            await test(db, owner, booking, provider, service);
        });
    }
    [SqlServerFact]
    public async Task Daily_summary_respects_Beirut_boundaries_and_past_task_status_without_sending_private_fields()
    {
        await Run(async (db, owner, booking, provider, service) =>
        {
            var summary = (await service.GenerateAsync(owner.Id, booking.Id, new(2026, 11, 20)))!;
            Assert.Equal("ready", summary.Status); Assert.Equal(5, summary.Sources.Count);
            Assert.Equal(1, summary.CompletedTasks); Assert.Equal(1, summary.PendingTasks); Assert.Equal(1, summary.Issues); Assert.Equal(1, summary.UndatedTasks);
            Assert.Equal(ScheduleMath.Utc(new(2026, 11, 21)), summary.StatusAsOf!.Value.ToUniversalTime());
            Assert.DoesNotContain(summary.Sources, s => s.Text.StartsWith("Next day"));
            Assert.Contains(summary.Sources, s => s.Text == "Refresh water" && s.CompletedAt == null);
            var payload = AssistantJson.Serialize(GroqCareSummaryClient.Payload(provider.Last!));
            Assert.DoesNotContain("PRIVATE", payload); Assert.DoesNotContain(booking.Id.ToString(), payload); Assert.DoesNotContain(owner.Id.ToString(), payload);
            Assert.Contains(summary.Sources, s => s.Text.Contains("contents have not been analyzed"));
            var issue = summary.Sources.Single(s => s.IsIssue); Assert.Contains("#care-update-", issue.OriginalUrl);
        });
    }
    [SqlServerFact]
    public async Task Entire_booking_includes_undated_items_and_record_changes_invalidate_cache()
    {
        await Run(async (db, owner, booking, provider, service) =>
        {
            var all = (await service.GenerateAsync(owner.Id, booking.Id, null))!;
            Assert.Equal(8, all.Sources.Count); Assert.Equal(2, all.CompletedTasks); Assert.Equal(2, all.PendingTasks);
            await service.GenerateAsync(owner.Id, booking.Id, null); Assert.Equal(1, provider.Calls);
            var update = await db.BookingUpdates.FirstAsync(u => u.BookingId == booking.Id && !u.IsIssue && u.Message != ""); update.Message = "Edited sitter update"; await db.SaveChangesAsync();
            var edited = (await service.GenerateAsync(owner.Id, booking.Id, null))!;
            Assert.Equal(2, provider.Calls); Assert.Contains(edited.Sources, s => s.Text == "Edited sitter update");
        });
    }
    [SqlServerFact]
    public async Task Other_accounts_and_inactive_owners_cannot_read_records_or_cached_summaries()
    {
        await Run(async (db, owner, booking, provider, service) =>
        {
            await service.GenerateAsync(owner.Id, booking.Id, null); Assert.Equal(1, provider.Calls);
            Assert.Null(await service.GenerateAsync(Guid.NewGuid(), booking.Id, null)); Assert.Equal(1, provider.Calls);
            owner.IsActive = false; await db.SaveChangesAsync();
            Assert.Null(await service.GenerateAsync(owner.Id, booking.Id, null)); Assert.Equal(1, provider.Calls);
        });
    }
    [SqlServerFact]
    public async Task Empty_and_future_periods_do_not_call_provider()
    {
        await Run(async (db, owner, booking, provider, service) =>
        {
            Assert.Equal("empty", (await service.GenerateAsync(owner.Id, booking.Id, new(2026, 11, 10)))!.Status);
            await Assert.ThrowsAsync<AssistantException>(() => service.GenerateAsync(owner.Id, booking.Id, new(2026, 11, 22)));
            await Assert.ThrowsAsync<AssistantException>(() => service.GenerateAsync(owner.Id, booking.Id, DateOnly.MinValue));
            Assert.Equal(0, provider.Calls);
        });
    }
}
