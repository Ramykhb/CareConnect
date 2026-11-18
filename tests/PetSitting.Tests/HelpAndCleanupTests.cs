using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Bookings;
using PetSitting.Services.Scheduling;
using PetSitting.ViewModels;
using Xunit;

namespace PetSitting.Tests;

// Help Center, deleting saved availability rules, and removing checklist tasks the sitter added.
public sealed class HelpAndCleanupTests
{
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));

    [Fact]
    public async Task Help_center_requires_sign_in()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/Help");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Identity/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public void Support_form_needs_a_subject_and_a_real_message()
    {
        var results = new List<ValidationResult>();
        var form = new SupportRequestForm { Subject = "", Message = "hi" };
        results.Clear(); Validator.TryValidateObject(form, new ValidationContext(form), results, true);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(SupportRequestForm.Subject)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(SupportRequestForm.Message)));
        var ok = new SupportRequestForm { Subject = "Payment question", Message = "How do refunds work after a cancellation?" };
        results.Clear(); Assert.True(Validator.TryValidateObject(ok, new ValidationContext(ok), results, true));
    }

    [SqlServerFact]
    public async Task Sitters_delete_rules_and_their_own_tasks_but_not_what_a_booking_relies_on()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        ApplicationUser NewUser(string name) => new() { Id = Guid.NewGuid(), FirstName = name, LastName = "Cleanup", UserName = "cleanup-" + Guid.NewGuid().ToString("N") };
        var owner = NewUser("Owner"); var sitter = NewUser("Sitter");
        var start = DateTimeOffset.UtcNow.AddDays(10);
        var profile = new PetSitterProfile { User = sitter, Bio = "Test", City = "Beirut", HourlyRate = 10 };
        // Base availability that does NOT cover the booking; an "available" rule opens that day.
        profile.Availability.Add(new SitterAvailability { StartsAtUtc = start.AddDays(5), EndsAtUtc = start.AddDays(6) });
        var day = ScheduleMath.LocalDate(start);
        var opening = new SitterScheduleException { StartsOn = day, EndsOn = day.AddDays(1), AllDay = true, IsAvailable = true };
        var blocking = new SitterScheduleException { StartsOn = day.AddDays(20), EndsOn = day.AddDays(20), AllDay = true, IsAvailable = false, Reason = "Trip" };
        profile.ScheduleExceptions.Add(opening); profile.ScheduleExceptions.Add(blocking);
        var request = new PetCareRequest { Customer = owner, Title = "Cleanup care", Description = "Test", City = "Beirut", StartsAtUtc = start, EndsAtUtc = start.AddHours(3), Budget = 30, Status = RequestStatus.Booked };
        var offer = new PetCareOffer { Request = request, SitterProfile = profile, Amount = 30, Status = OfferStatus.Accepted };
        var booking = new PetBooking { Request = request, Offer = offer, Customer = owner, SitterProfile = profile, StartsAtUtc = start, EndsAtUtc = start.AddHours(3), TotalAmount = 30, Status = BookingStatus.Confirmed };
        booking.CareTasks.Add(new BookingCareTask { Key = "custom:" + Guid.NewGuid().ToString("N"), Label = "Water plants" });
        booking.CareTasks.Add(new BookingCareTask { Key = "note-derived", Label = "From the care notes" });
        db.AddRange(owner, sitter, profile, request, offer, booking);
        await db.SaveChangesAsync();
        try
        {
            var schedule = new SitterAvailabilityService(db, TimeProvider.System);
            string Version() => Convert.ToBase64String(db.PetSitterProfiles.AsNoTracking().Single(p => p.Id == profile.Id).RowVersion);
            Assert.False((await schedule.DeleteRuleAsync(sitter.Id, blocking.Id, Convert.ToBase64String(new byte[8]))).Succeeded); // stale page
            Assert.False((await schedule.DeleteRuleAsync(owner.Id, blocking.Id, Version())).Succeeded);                            // not a sitter
            var refused = await schedule.DeleteRuleAsync(sitter.Id, opening.Id, Version());                                       // the booking relies on it
            Assert.False(refused.Succeeded);
            Assert.Contains("active booking", refused.Message);
            Assert.True((await schedule.DeleteRuleAsync(sitter.Id, blocking.Id, Version())).Succeeded);
            Assert.False(await db.SitterScheduleExceptions.AnyAsync(e => e.Id == blocking.Id));
            Assert.True(await db.SitterScheduleExceptions.AnyAsync(e => e.Id == opening.Id));

            var bookings = new SitterBookingService(db, TimeProvider.System);
            var custom = booking.CareTasks.First(t => t.Key.StartsWith("custom:")).Key;
            Assert.False((await bookings.RemoveTaskAsync(sitter.Id, new CareTaskForm { Id = booking.Id, Key = "note-derived" })).Succeeded);
            Assert.True((await bookings.RemoveTaskAsync(owner.Id, new CareTaskForm { Id = booking.Id, Key = custom })).NotFound);
            Assert.True((await bookings.RemoveTaskAsync(sitter.Id, new CareTaskForm { Id = booking.Id, Key = custom })).Succeeded);
            Assert.False(await db.BookingCareTasks.AnyAsync(t => t.Key == custom));
            Assert.True(await db.BookingCareTasks.AnyAsync(t => t.BookingId == booking.Id && t.Key == "note-derived"));
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.BookingCareTasks.Where(t => t.BookingId == booking.Id).ExecuteDeleteAsync();
            await db.PetBookings.Where(b => b.Id == booking.Id).ExecuteDeleteAsync();
            await db.PetCareOffers.Where(o => o.Id == offer.Id).ExecuteDeleteAsync();
            await db.PetCareRequests.Where(r => r.Id == request.Id).ExecuteDeleteAsync();
            await db.SitterScheduleExceptions.Where(e => e.SitterProfileId == profile.Id).ExecuteDeleteAsync();
            await db.SitterAvailabilities.Where(a => a.SitterProfileId == profile.Id).ExecuteDeleteAsync();
            await db.PetSitterProfiles.Where(p => p.Id == profile.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == sitter.Id).ExecuteDeleteAsync();
        }
    }
}
