using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Bookings;
using Xunit;

namespace PetSitting.Tests;

// Sitters get "Care starts soon" 30 minutes before a paid booking and "Time to start care" at the start, each once.
public sealed class CareReminderTests
{
    [SqlServerFact]
    public async Task Paid_bookings_remind_the_sitter_once_before_and_once_at_the_start()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        ApplicationUser NewUser(string name) => new() { Id = Guid.NewGuid(), FirstName = name, LastName = "Reminder", UserName = "reminder-" + Guid.NewGuid().ToString("N") };
        var customer = NewUser("Customer"); var sitter = NewUser("Sitter");
        var profile = new PetSitterProfile { User = sitter, Bio = "Test", City = "Beirut", HourlyRate = 10 };
        var now = DateTimeOffset.UtcNow;
        PetBooking Booking(BookingStatus status, int minutes)
        {
            var start = now.AddMinutes(minutes);
            var request = new PetCareRequest { Customer = customer, Title = "Reminder test", Description = "Test", City = "Beirut", StartsAtUtc = start, EndsAtUtc = start.AddHours(2), Budget = 20, Status = RequestStatus.Booked };
            return new PetBooking { Request = request, Offer = new PetCareOffer { Request = request, SitterProfile = profile, Amount = 20, Status = OfferStatus.Accepted },
                Customer = customer, SitterProfile = profile, StartsAtUtc = start, EndsAtUtc = start.AddHours(2), TotalAmount = 20, Status = status };
        }
        var paid = Booking(BookingStatus.Confirmed, 10);
        var unpaid = Booking(BookingStatus.AwaitingPayment, 10);
        var later = Booking(BookingStatus.Confirmed, 120);
        db.AddRange(customer, sitter, profile, paid, unpaid, later);
        await db.SaveChangesAsync();
        try
        {
            Assert.Equal(1, await CareReminderService.RunAsync(db, now, bookingId: paid.Id));
            Assert.Equal(0, await CareReminderService.RunAsync(db, now.AddMinutes(1), bookingId: paid.Id));      // not sent twice
            Assert.Equal(0, await CareReminderService.RunAsync(db, now, bookingId: unpaid.Id));                 // not paid yet
            Assert.Equal(0, await CareReminderService.RunAsync(db, now, bookingId: later.Id));                  // more than 30 minutes away
            Assert.Equal(1, await CareReminderService.RunAsync(db, now.AddMinutes(11), bookingId: paid.Id));    // start time reached
            Assert.Equal(0, await CareReminderService.RunAsync(db, now.AddMinutes(12), bookingId: paid.Id));
            Assert.Equal(0, await CareReminderService.RunAsync(db, now.AddHours(3), bookingId: paid.Id));       // booking over
            db.ChangeTracker.Clear();
            var titles = await db.Notifications.AsNoTracking().Where(n => n.UserId == sitter.Id).OrderBy(n => n.CreatedAtUtc).Select(n => n.Title + "|" + n.RelativeUrl).ToListAsync();
            Assert.Equal(["Care starts soon|/SitterBookings/Details/" + paid.Id, "Time to start care|/SitterBookings/Details/" + paid.Id], titles);
        }
        finally
        {
            db.ChangeTracker.Clear();
            var ids = new[] { paid.RequestId, unpaid.RequestId, later.RequestId };
            await db.Notifications.Where(n => n.UserId == sitter.Id || n.UserId == customer.Id).ExecuteDeleteAsync();
            await db.PetBookings.Where(b => b.CustomerId == customer.Id).ExecuteDeleteAsync();
            await db.PetCareOffers.Where(o => ids.Contains(o.RequestId)).ExecuteDeleteAsync();
            await db.PetCareRequests.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync();
            await db.PetSitterProfiles.Where(p => p.Id == profile.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == customer.Id || u.Id == sitter.Id).ExecuteDeleteAsync();
        }
    }
}
