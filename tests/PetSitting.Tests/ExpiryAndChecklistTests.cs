using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Bookings;
using Xunit;

namespace PetSitting.Tests;

// #8 expiry, payment deadline and no-shows; #9 the checklist is copied when care starts.
public sealed class ExpiryAndChecklistTests
{
    [Fact]
    public void Care_cannot_start_after_the_end_time()
    {
        var now = DateTimeOffset.UtcNow;
        var booking = new PetBooking { Status = BookingStatus.Confirmed, TotalAmount = 0, StartsAtUtc = now.AddHours(-3), EndsAtUtc = now.AddHours(-1) };
        Assert.Contains("end time has passed", BookingRules.StartBlock(booking, now));
        booking.EndsAtUtc = now.AddHours(1);
        Assert.Null(BookingRules.StartBlock(booking, now));
    }

    [Fact]
    public void Editing_a_note_after_care_started_does_not_change_the_checklist()
    {
        var now = DateTimeOffset.UtcNow;
        var pet = new Pet { Name = "Rex", MedicalNotes = "One pill with dinner" };
        var booking = new PetBooking { Request = new PetCareRequest { Pets = [new PetCareRequestPet { Pet = pet }] } };
        var before = BookingRules.Tasks(booking).Single();
        BookingRules.SaveChecklist(booking, now);
        var saved = Assert.Single(booking.CareTasks);
        saved.CompletedAtUtc = now;                                   // the sitter ticked it
        pet.MedicalNotes = "Two pills now";                                                         // the customer edits the note later
        var after = BookingRules.Tasks(booking).Single();
        Assert.Equal(before.Key, after.Key);
        Assert.Equal(before.Note, after.Note);                          // still the note care started with
        Assert.True(after.Completed);                                   // and still ticked
        BookingRules.SaveChecklist(booking, now.AddHours(1));           // saving again changes nothing
        Assert.Equal(now, booking.ChecklistSavedAtUtc);
    }

    [SqlServerFact]
    public async Task Overdue_requests_and_bookings_are_saved_as_expired_or_cancelled()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        var now = DateTimeOffset.UtcNow;
        ApplicationUser NewUser(string name) => new() { Id = Guid.NewGuid(), FirstName = name, LastName = "Expiry", UserName = "expiry-" + Guid.NewGuid().ToString("N") };
        var customer = NewUser("Customer"); var sitter = NewUser("Sitter");
        var profile = new PetSitterProfile { User = sitter, Bio = "Test", City = "Beirut", HourlyRate = 10 };
        PetCareRequest Request(string title, DateTimeOffset start, RequestStatus status) => new() { Customer = customer, Title = title, Description = "Test", City = "Beirut", StartsAtUtc = start, EndsAtUtc = start.AddHours(2), Budget = 20, Status = status };
        var open = Request("No sitter", now.AddHours(-1), RequestStatus.Open);
        var invite = new PetCareOffer { Request = open, SitterProfile = profile, Amount = 20 };
        var future = Request("Still ahead", now.AddDays(2), RequestStatus.Open);
        PetBooking Booking(PetCareRequest r, BookingStatus status, DateTimeOffset start) => new() { Request = r, Offer = new PetCareOffer { Request = r, SitterProfile = profile, Amount = 20, Status = OfferStatus.Accepted },
            Customer = customer, SitterProfile = profile, StartsAtUtc = start, EndsAtUtc = start.AddHours(2), TotalAmount = 20, Status = status };
        var unpaidRequest = Request("Unpaid", now.AddMinutes(-30), RequestStatus.Booked);
        var unpaid = Booking(unpaidRequest, BookingStatus.AwaitingPayment, now.AddMinutes(-30));
        var noShowRequest = Request("No show", now.AddHours(-5), RequestStatus.Booked);
        var noShow = Booking(noShowRequest, BookingStatus.Confirmed, now.AddHours(-5));
        var payment = new Payment { Booking = noShow, PayerId = customer.Id, Amount = 20, Currency = "USD", Status = PaymentStatus.Succeeded, Provider = "Dummy", ProviderTransactionId = "T-" + Guid.NewGuid().ToString("N")[..8], IdempotencyKey = Guid.NewGuid(), PaidAtUtc = now.AddDays(-1) };
        var upcomingRequest = Request("Upcoming", now.AddHours(3), RequestStatus.Booked);
        var upcoming = Booking(upcomingRequest, BookingStatus.Confirmed, now.AddHours(3));
        db.AddRange(customer, sitter, profile, open, invite, future, unpaidRequest, unpaid, noShowRequest, noShow, payment, upcomingRequest, upcoming);
        await db.SaveChangesAsync();
        var requestIds = new[] { open.Id, future.Id, unpaidRequest.Id, noShowRequest.Id, upcomingRequest.Id };
        try
        {
            Assert.Equal(3, await BookingExpiryService.RunAsync(db, now, customerId: customer.Id));
            db.ChangeTracker.Clear();
            var requests = await db.PetCareRequests.AsNoTracking().Where(r => requestIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Status);
            Assert.Equal(RequestStatus.Expired, requests[open.Id]);
            Assert.Equal(RequestStatus.Open, requests[future.Id]);
            Assert.Equal(RequestStatus.Booked, requests[upcomingRequest.Id]);
            Assert.Equal(OfferStatus.Expired, (await db.PetCareOffers.AsNoTracking().SingleAsync(o => o.Id == invite.Id)).Status);
            var bookings = await db.PetBookings.AsNoTracking().Where(b => b.CustomerId == customer.Id).ToDictionaryAsync(b => b.Id);
            Assert.Equal(BookingStatus.Cancelled, bookings[unpaid.Id].Status);
            Assert.Contains("Payment", bookings[unpaid.Id].CancellationReason);
            Assert.Equal(BookingStatus.Cancelled, bookings[noShow.Id].Status);
            Assert.Equal(BookingStatus.Confirmed, bookings[upcoming.Id].Status);
            Assert.Equal(PaymentStatus.Refunded, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id)).Status);
            Assert.True(await db.Notifications.AnyAsync(n => n.UserId == customer.Id && n.Title == "Your request expired"));
            Assert.Equal(0, await BookingExpiryService.RunAsync(db, now, customerId: customer.Id));                 // nothing left to do
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Notifications.Where(n => n.UserId == customer.Id || n.UserId == sitter.Id).ExecuteDeleteAsync();
            await db.Payments.Where(p => p.PayerId == customer.Id).ExecuteDeleteAsync();
            await db.PetBookings.Where(b => b.CustomerId == customer.Id).ExecuteDeleteAsync();
            await db.PetCareOffers.Where(o => requestIds.Contains(o.RequestId)).ExecuteDeleteAsync();
            await db.PetCareRequests.Where(r => requestIds.Contains(r.Id)).ExecuteDeleteAsync();
            await db.PetSitterProfiles.Where(p => p.Id == profile.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == customer.Id || u.Id == sitter.Id).ExecuteDeleteAsync();
        }
    }
}
