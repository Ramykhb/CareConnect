using System.Net;
using System.Text.Json;
using CareConnect.Contracts.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Messaging;
using PetSitting.Services.Portal;
using Xunit;

namespace PetSitting.Tests;

// #3 payment results go to the other app as a "Payment" message; #5 the manager overview page.
public sealed class PaymentResultsAndReportsTests
{
    private static CombinedCareEvent Event(string type, string? payment, long revision = 1, Guid? correlation = null, Guid? peerRequest = null) =>
        new(new(Guid.NewGuid(), correlation ?? Guid.NewGuid(), type, 1, "BabySitting", DateTimeOffset.UtcNow), peerRequest ?? Guid.NewGuid(), null, revision,
            "Booked", "Confirmed", PaymentStatus: payment);

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Refunded")]
    public void Payment_results_are_accepted(string status) => IntegrationStore.Validate(Event("Payment", status));

    [Theory]
    [InlineData("Payment", null)]
    [InlineData("Payment", "Pending")]
    [InlineData("Payment", "Paid")]
    [InlineData("State", "Succeeded")]
    public void Bad_payment_results_are_rejected(string type, string? status) =>
        Assert.Throws<InvalidDataException>(() => IntegrationStore.Validate(Event(type, status)));

    [Fact]
    public void Csv_download_quotes_names_and_blocks_formulas()
    {
        var report = new ManagerReport(30, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, 3, 2, 1, 0, 4, 5, [new("Open", 5)], 2, [new("Completed", 1)], 1, 0,
            125.5m, 10m, 1, 20m, 0m, 0, 1, 4.5, 0, 0, 0, 0, [new("Jan 2026", 2, 125.5m)], [new("=HYPERLINK(\"x\")", 1, 50m, 5)]);
        var csv = new ManagerReportService(null!, TimeProvider.System).ToCsv(report);
        Assert.Contains("\"Money received (USD)\",125.50", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\",1,50.00,5", csv);   // quoted, and Excel will not run it
        Assert.Equal(50, report.CompletionRate);
    }

    [SqlServerFact]
    public async Task Paying_a_linked_booking_queues_a_payment_message_for_the_other_app()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        var d = Data.Create();
        var link = new CombinedCareLink { LocalRequestId = d.Request.Id, PeerRequestId = Guid.NewGuid(), OriginPlatform = "PetSitting",
            InvitationHash = new string('a', 64), InvitationExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) };
        d.Request.CorrelationId = link.Id;                                             // set by the app when two requests are linked
        db.AddRange(d.Customer, d.Sitter, d.Profile, d.Request, d.Booking, link);
        await db.SaveChangesAsync();
        try
        {
            d.Booking.Status = BookingStatus.Confirmed;
            db.Payments.Add(new Payment { Booking = d.Booking, PayerId = d.Customer.Id, Amount = 40, Status = PaymentStatus.Succeeded,
                ProviderTransactionId = "T-" + Guid.NewGuid().ToString("N")[..8], PaidAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            var sent = await db.MessageOutboxEntries.AsNoTracking().Where(m => m.CorrelationId == link.Id).ToListAsync();
            var payment = Assert.Single(sent, m => m.EventType == "Payment");
            Assert.Equal("petsitting.combined.payment", payment.RoutingKey);
            var message = JsonSerializer.Deserialize<CombinedCareEvent>(payment.PayloadJson)!;
            Assert.Equal("Succeeded", message.PaymentStatus);
            Assert.Equal("Confirmed", message.BookingStatus);
            Assert.DoesNotContain("Amount", payment.PayloadJson, StringComparison.OrdinalIgnoreCase);   // no amount is shared
            // One state message when the booking was created, one when it was confirmed; the payment comes after both.
            var states = sent.Where(m => m.EventType == "State").Select(m => JsonSerializer.Deserialize<CombinedCareEvent>(m.PayloadJson)!).ToList();
            Assert.Equal(2, states.Count);
            Assert.True(message.Revision > states.Max(s => s.Revision));
        }
        finally { await Cleanup(db, d, link.Id); }
    }

    [SqlServerFact]
    public async Task A_payment_result_from_the_other_app_is_saved_once_and_the_customer_is_told()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        var d = Data.Create();
        var peerRequest = Guid.NewGuid();
        var link = new CombinedCareLink { LocalRequestId = d.Request.Id, PeerRequestId = peerRequest, OriginPlatform = "PetSitting",
            InvitationHash = new string('b', 64), InvitationExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) };
        d.Request.CorrelationId = link.Id;
        db.AddRange(d.Customer, d.Request, link);
        await db.SaveChangesAsync();
        try
        {
            var store = new IntegrationStore(db);
            var paid = Event("Payment", "Succeeded", 3, link.Id, peerRequest);
            await store.ReceiveAsync(paid, default);
            await store.ReceiveAsync(paid, default);                                   // a replay changes nothing
            await store.ReceiveAsync(Event("Payment", "Failed", 2, link.Id, peerRequest), default);   // an older result is ignored
            db.ChangeTracker.Clear();
            var saved = await db.CombinedCareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id);
            Assert.Equal("Succeeded", saved.PeerPaymentStatus);
            Assert.Equal(3, saved.PeerPaymentRevision);
            var alert = Assert.Single(await db.Notifications.AsNoTracking().Where(n => n.UserId == d.Customer.Id && n.Category == NotificationCategory.Payment).ToListAsync());
            Assert.Equal("Baby care payment received", alert.Title);

            await store.ReceiveAsync(Event("Payment", "Refunded", 4, link.Id, peerRequest), default);
            db.ChangeTracker.Clear();
            Assert.Equal("Refunded", (await db.CombinedCareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id)).PeerPaymentStatus);
        }
        finally { await Cleanup(db, d, link.Id); }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [SqlServerFact]
    public async Task The_overview_counts_new_bookings_money_and_reviews()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        // The report "today" is in 2091, so only this test's rows fall inside its period, even while other tests run.
        var now = new DateTimeOffset(2091, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var then = now.AddDays(-2);
        var reports = new ManagerReportService(db, new FixedClock(now));
        var d = Data.Create();
        d.Customer.CreatedAtUtc = then; d.Sitter.CreatedAtUtc = then;
        d.Request.CreatedAtUtc = then; d.Booking.CreatedAtUtc = then;
        d.Booking.Status = BookingStatus.Completed;
        d.Booking.CompletedAtUtc = then;
        db.AddRange(d.Customer, d.Sitter, d.Profile, d.Request, d.Booking,
            new Payment { Booking = d.Booking, PayerId = d.Customer.Id, Amount = 40, Status = PaymentStatus.Succeeded, ProviderTransactionId = "T-" + Guid.NewGuid().ToString("N")[..8], PaidAtUtc = then, CreatedAtUtc = then },
            new Review { Booking = d.Booking, ReviewerId = d.Customer.Id, Rating = 5, CreatedAtUtc = then });
        await db.SaveChangesAsync();
        try
        {
            var report = await reports.ReportAsync(30);
            Assert.Equal(1, report.NewRequests);
            Assert.Equal(1, report.NewBookings);
            Assert.Equal(1, report.CompletedBookings);
            Assert.Equal(100, report.CompletionRate);
            Assert.Equal(40, report.Revenue);
            Assert.Equal(1, report.Reviews);
            Assert.Equal(5, report.AverageRating);
            Assert.Equal(2, report.NewAccounts);
            var top = Assert.Single(report.TopSitters);
            Assert.Equal(("Sitter Report", 1, 40m, 5.0), (top.Name, top.Completed, top.Earned, top.Rating));
            Assert.Equal(6, report.Months.Count);
            Assert.Equal(("Jun 2091", 1, 40m), (report.Months[^1].Label, report.Months[^1].Bookings, report.Months[^1].Revenue));
            Assert.Equal(0, (await new ManagerReportService(db, new FixedClock(now.AddDays(40))).ReportAsync(30)).NewBookings);   // outside the period
            Assert.Equal(30, (await reports.ReportAsync(12345)).Days);                 // unknown periods fall back to 30 days
            Assert.Contains("\"New bookings\",1", reports.ToCsv(report));
        }
        finally { await Cleanup(db, d, Guid.Empty); }
    }

    [SqlServerFact]
    public async Task Only_managers_can_open_the_overview()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var url in new[] { "/ManagerReports", "/ManagerReports/Export?days=30" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("Login", response.Headers.Location!.ToString());
        }
    }

    private sealed record Data(ApplicationUser Customer, ApplicationUser Sitter, PetSitterProfile Profile, PetCareRequest Request, PetBooking Booking)
    {
        public static Data Create()
        {
            ApplicationUser NewUser(string name) => new() { Id = Guid.NewGuid(), FirstName = name, LastName = "Report", UserName = "report-" + Guid.NewGuid().ToString("N") };
            var customer = NewUser("Customer"); var sitter = NewUser("Sitter");
            var profile = new PetSitterProfile { User = sitter, Bio = "Test", City = "Beirut", HourlyRate = 10 };
            var start = DateTimeOffset.UtcNow.AddDays(2);
            var request = new PetCareRequest { Customer = customer, Title = "Payment test", Description = "Test", City = "Beirut", StartsAtUtc = start, EndsAtUtc = start.AddHours(2), Budget = 40, Status = RequestStatus.Booked };
            var booking = new PetBooking { Request = request, Offer = new PetCareOffer { Request = request, SitterProfile = profile, Amount = 40, Status = OfferStatus.Accepted },
                Customer = customer, SitterProfile = profile, StartsAtUtc = start, EndsAtUtc = start.AddHours(2), TotalAmount = 40, Status = BookingStatus.AwaitingPayment };
            return new(customer, sitter, profile, request, booking);
        }
    }

    private static async Task Cleanup(PetSittingDbContext db, Data d, Guid linkId)
    {
        db.ChangeTracker.Clear();
        var userIds = new[] { d.Customer.Id, d.Sitter.Id };
        await db.Notifications.Where(n => userIds.Contains(n.UserId)).ExecuteDeleteAsync();
        await db.MessageOutboxEntries.Where(m => m.CorrelationId == linkId).ExecuteDeleteAsync();
        await db.ProcessedMessages.Where(m => m.CorrelationId == linkId).ExecuteDeleteAsync();
        await db.CombinedCareLinks.Where(l => l.Id == linkId).ExecuteDeleteAsync();
        await db.Reviews.Where(r => r.ReviewerId == d.Customer.Id).ExecuteDeleteAsync();
        await db.Payments.Where(p => p.PayerId == d.Customer.Id).ExecuteDeleteAsync();
        await db.PetBookings.Where(b => b.CustomerId == d.Customer.Id).ExecuteDeleteAsync();
        await db.PetCareOffers.Where(o => o.RequestId == d.Request.Id).ExecuteDeleteAsync();
        await db.PetCareRequests.Where(r => r.Id == d.Request.Id).ExecuteDeleteAsync();
        await db.PetSitterProfiles.Where(p => p.Id == d.Profile.Id).ExecuteDeleteAsync();
        await db.Users.Where(u => userIds.Contains(u.Id)).ExecuteDeleteAsync();
    }
}
