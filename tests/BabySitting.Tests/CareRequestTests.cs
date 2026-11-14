using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Repositories;
using BabySitting.Services.Bookings;
using BabySitting.ViewModels;
using Xunit;

namespace BabySitting.Tests;

public sealed class CareRequestTests
{
    private static WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

    private static CareRequestFormViewModel ValidForm()
    {
        var start = DashboardViewModel.Local(DateTimeOffset.UtcNow).DateTime.Date.AddDays(2).AddHours(18);
        return new() { Title = "Evening care", Description = "Dinner, bath and bed.", City = "Beirut", Budget = 40, NumberOfChildren = 2, YoungestChildAgeYears = 3, OldestChildAgeYears = 7, StartsAtLocal = start, EndsAtLocal = start.AddHours(4) };
    }

    private static HashSet<string> Invalid(CareRequestFormViewModel form)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(form, new ValidationContext(form), results, true);
        return results.SelectMany(r => r.MemberNames).ToHashSet();
    }

    [Theory]
    [InlineData("/CareRequests")]
    [InlineData("/CareRequests/Create")]
    public async Task My_requests_require_a_signed_in_parent(string path)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Identity/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public void Request_form_accepts_a_complete_request() => Assert.Empty(Invalid(ValidForm()));

    [Fact]
    public void Request_form_rejects_past_reversed_and_overlong_times()
    {
        var past = ValidForm(); past.StartsAtLocal = DateTime.Now.AddDays(-1); past.EndsAtLocal = past.StartsAtLocal.Value.AddHours(2);
        Assert.Contains(nameof(CareRequestFormViewModel.StartsAtLocal), Invalid(past));
        var reversed = ValidForm(); reversed.EndsAtLocal = reversed.StartsAtLocal!.Value.AddHours(-1);
        Assert.Contains(nameof(CareRequestFormViewModel.EndsAtLocal), Invalid(reversed));
        var tooLong = ValidForm(); tooLong.EndsAtLocal = tooLong.StartsAtLocal!.Value.AddDays(CareRequestFormViewModel.MaxDays + 1);
        Assert.Contains(nameof(CareRequestFormViewModel.EndsAtLocal), Invalid(tooLong));
    }

    [Fact]
    public void Request_form_rejects_missing_fields_and_inconsistent_ages()
    {
        var form = ValidForm(); form.Title = ""; form.Description = ""; form.City = ""; form.Budget = -1;
        var invalid = Invalid(form);
        foreach (var name in new[] { nameof(form.Title), nameof(form.Description), nameof(form.City), nameof(form.Budget) })
            Assert.Contains(name, invalid);
        // Cross-field rules run only once the attributes pass, so check them directly (as in FamilyTests).
        var ages = ValidForm(); ages.YoungestChildAgeYears = 9; ages.OldestChildAgeYears = 4;
        Assert.Contains(ages.Validate(new ValidationContext(ages)), r => r.MemberNames.Contains(nameof(ages.OldestChildAgeYears)));
    }

    // SQL Server tests (CARECONNECT_SQL_TESTS=1) create uniquely named records and delete them afterwards.
    private sealed class Data : IAsyncDisposable
    {
        public required WebApplicationFactory<Program> Factory { get; init; }
        public ApplicationUser Parent { get; } = new() { Id = Guid.NewGuid(), FirstName = "Parent", LastName = "Test", UserName = "parent-" + Guid.NewGuid().ToString("N") };
        public ApplicationUser Stranger { get; } = new() { Id = Guid.NewGuid(), FirstName = "Other", LastName = "Parent", UserName = "other-" + Guid.NewGuid().ToString("N") };
        public ApplicationUser Sitter { get; } = new() { Id = Guid.NewGuid(), FirstName = "Sitter", LastName = "Test", UserName = "sitter-" + Guid.NewGuid().ToString("N") };
        public Guid RequestId { get; private set; }
        public Guid BookingId { get; private set; }

        public static async Task<Data> CreateAsync(bool booked, BookingStatus status = BookingStatus.Confirmed, bool paid = true)
        {
            var data = new Data { Factory = CreateFactory() };
            await data.UseAsync(async db =>
            {
                var city = "req-" + Guid.NewGuid().ToString("N")[..12];
                db.Users.AddRange(data.Parent, data.Stranger, data.Sitter);
                var family = new FamilyProfile { User = data.Parent, City = city, NumberOfChildren = 2, YoungestChildAgeYears = 3, OldestChildAgeYears = 7 };
                var profile = new BabysitterProfile { User = data.Sitter, Bio = "Test", City = city, HourlyRate = 10 };
                var start = DateTimeOffset.UtcNow.AddDays(3);
                var request = new ChildcareRequest { Customer = data.Parent, FamilyProfile = family, Title = "Evening care", Description = "Test", City = city, StartsAtUtc = start, EndsAtUtc = start.AddHours(4), Budget = 40, NumberOfChildren = 2, YoungestChildAgeYears = 3, OldestChildAgeYears = 7, Status = booked ? RequestStatus.Booked : RequestStatus.Open };
                db.AddRange(family, profile, request);
                if (booked)
                {
                    var offer = new BabysittingOffer { Request = request, SitterProfile = profile, Amount = 40, Status = OfferStatus.Accepted };
                    var booking = new BabysittingBooking { Request = request, Offer = offer, Customer = data.Parent, SitterProfile = profile, StartsAtUtc = request.StartsAtUtc, EndsAtUtc = request.EndsAtUtc, TotalAmount = 40, Status = status };
                    db.AddRange(offer, booking);
                    if (paid) db.Payments.Add(new Payment { Booking = booking, Payer = data.Parent, Amount = 40, Status = PaymentStatus.Succeeded });
                    data.BookingId = booking.Id;
                }
                await db.SaveChangesAsync();
                data.RequestId = request.Id;
            });
            return data;
        }

        public async Task UseAsync(Func<BabySittingDbContext, Task> action)
        {
            using var scope = Factory.Services.CreateScope();
            await action(scope.ServiceProvider.GetRequiredService<BabySittingDbContext>());
        }

        public async Task<T> GetAsync<T>(Func<BabySittingDbContext, Task<T>> query)
        {
            using var scope = Factory.Services.CreateScope();
            return await query(scope.ServiceProvider.GetRequiredService<BabySittingDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            var users = new[] { Parent.Id, Stranger.Id, Sitter.Id };
            await UseAsync(async db =>
            {
                await db.Notifications.Where(n => users.Contains(n.UserId)).ExecuteDeleteAsync();
                await db.Reviews.Where(r => users.Contains(r.ReviewerId)).ExecuteDeleteAsync();
                await db.Payments.Where(p => users.Contains(p.PayerId)).ExecuteDeleteAsync();
                await db.BabysittingBookings.Where(b => users.Contains(b.CustomerId)).ExecuteDeleteAsync();
                await db.BabysittingOffers.Where(o => users.Contains(o.SitterProfile.UserId)).ExecuteDeleteAsync();
                await db.ChildcareRequests.Where(r => r.CustomerId != null && users.Contains(r.CustomerId.Value)).ExecuteDeleteAsync();
                await db.FamilyProfiles.Where(f => users.Contains(f.UserId)).ExecuteDeleteAsync();
                await db.BabysitterProfiles.Where(p => users.Contains(p.UserId)).ExecuteDeleteAsync();
                await db.Users.Where(u => users.Contains(u.Id)).ExecuteDeleteAsync();
            });
            await Factory.DisposeAsync();
        }
    }

    [SqlServerFact]
    public async Task Requests_are_scoped_to_their_parent_and_edits_detect_stale_versions()
    {
        await using var d = await Data.CreateAsync(booked: false);
        await d.UseAsync(async db =>
        {
            var repo = new CareRequestRepository(db);
            Assert.Single(await repo.GetAllForParentAsync(d.Parent.Id));
            Assert.Null(await repo.GetByIdAsync(d.RequestId, d.Stranger.Id));
            var current = await repo.GetByIdAsync(d.RequestId, d.Parent.Id);
            Assert.Equal(CareRequestWriteResult.NotFound, await repo.UpdateAsync(d.Stranger.Id, d.RequestId, current!.RowVersion, r => r.Title = "Hacked", DateTimeOffset.UtcNow));
            Assert.Equal(CareRequestWriteResult.Saved, await repo.UpdateAsync(d.Parent.Id, d.RequestId, current.RowVersion, r => r.Title = "Updated", DateTimeOffset.UtcNow));
            // The save changed the row version, so a second edit from the old page is rejected.
            Assert.Equal(CareRequestWriteResult.Conflict, await new CareRequestRepository(db).UpdateAsync(d.Parent.Id, d.RequestId, current.RowVersion, r => r.Title = "Stale", DateTimeOffset.UtcNow));
        });
        Assert.Equal("Updated", await d.GetAsync(db => db.ChildcareRequests.Where(r => r.Id == d.RequestId).Select(r => r.Title).SingleAsync()));
    }

    [SqlServerFact]
    public async Task A_request_without_responses_is_deleted_but_a_booked_one_is_kept()
    {
        await using (var open = await Data.CreateAsync(booked: false))
        {
            await open.UseAsync(async db => Assert.Equal(CareRequestWriteResult.Deleted, await new CareRequestRepository(db).DeleteOrCancelAsync(open.Parent.Id, open.RequestId)));
            Assert.False(await open.GetAsync(db => db.ChildcareRequests.AnyAsync(r => r.Id == open.RequestId)));
        }
        await using var booked = await Data.CreateAsync(booked: true);
        await booked.UseAsync(async db =>
        {
            var repo = new CareRequestRepository(db);
            Assert.Equal(CareRequestWriteResult.NotEditable, await repo.DeleteOrCancelAsync(booked.Parent.Id, booked.RequestId));
            var request = await repo.GetByIdAsync(booked.RequestId, booked.Parent.Id);
            Assert.Equal(CareRequestWriteResult.NotEditable, await repo.UpdateAsync(booked.Parent.Id, booked.RequestId, request!.RowVersion, r => r.Title = "x", DateTimeOffset.UtcNow));
        });
    }

    private static Task<BookingWriteResult> Cancel(Data d, Guid userId, string? version = null) => d.GetAsync(async db =>
    {
        var v = version ?? Convert.ToBase64String((await db.BabysittingBookings.AsNoTracking().SingleAsync(b => b.Id == d.BookingId)).RowVersion);
        return await new BookingCancellationService(db, TimeProvider.System).CancelAsync(userId, d.BookingId, "Plans changed", v);
    });

    [SqlServerFact]
    public async Task Parent_cancel_closes_the_request_refunds_and_notifies_the_sitter()
    {
        await using var d = await Data.CreateAsync(booked: true);
        Assert.True((await Cancel(d, d.Stranger.Id)).NotFound);
        Assert.True((await Cancel(d, d.Parent.Id)).Succeeded);
        Assert.Equal(BookingStatus.Cancelled, await d.GetAsync(db => db.BabysittingBookings.Where(b => b.Id == d.BookingId).Select(b => b.Status).SingleAsync()));
        Assert.Equal(RequestStatus.Cancelled, await d.GetAsync(db => db.ChildcareRequests.Where(r => r.Id == d.RequestId).Select(r => r.Status).SingleAsync()));
        Assert.Equal(PaymentStatus.Refunded, await d.GetAsync(db => db.Payments.Where(p => p.BookingId == d.BookingId).Select(p => p.Status).SingleAsync()));
        Assert.Equal(1, await d.GetAsync(db => db.Notifications.CountAsync(n => n.UserId == d.Sitter.Id)));
        Assert.True((await Cancel(d, d.Parent.Id)).Succeeded); // repeating is harmless
        Assert.Equal(1, await d.GetAsync(db => db.Notifications.CountAsync(n => n.UserId == d.Sitter.Id)));
    }

    [SqlServerFact]
    public async Task Sitter_cancel_withdraws_the_offer_and_reopens_the_request()
    {
        await using var d = await Data.CreateAsync(booked: true);
        Assert.True((await Cancel(d, d.Sitter.Id)).Succeeded);
        Assert.Equal(RequestStatus.Open, await d.GetAsync(db => db.ChildcareRequests.Where(r => r.Id == d.RequestId).Select(r => r.Status).SingleAsync()));
        Assert.Equal(OfferStatus.Withdrawn, await d.GetAsync(db => db.BabysittingOffers.Where(o => o.RequestId == d.RequestId).Select(o => o.Status).SingleAsync()));
        Assert.Equal(1, await d.GetAsync(db => db.Notifications.CountAsync(n => n.UserId == d.Parent.Id)));
    }

    private static Task<BookingWriteResult> Pay(Data d, Guid userId, Guid token) => d.GetAsync(async db =>
    {
        var v = Convert.ToBase64String((await db.BabysittingBookings.AsNoTracking().SingleAsync(b => b.Id == d.BookingId)).RowVersion);
        return await new ParentBookingService(db, TimeProvider.System).PayAsync(userId, d.BookingId, token, v);
    });

    [SqlServerFact]
    public async Task Demo_payment_confirms_the_booking_once_and_notifies_the_sitter()
    {
        await using var d = await Data.CreateAsync(booked: true, BookingStatus.AwaitingPayment, paid: false);
        var token = Guid.NewGuid();
        Assert.True((await Pay(d, d.Stranger.Id, token)).NotFound);
        Assert.True((await Pay(d, d.Parent.Id, token)).Succeeded);
        Assert.True((await Pay(d, d.Parent.Id, token)).Succeeded); // a resubmitted form is not charged twice
        Assert.False((await Pay(d, d.Parent.Id, Guid.NewGuid())).Succeeded); // already paid
        Assert.Equal(BookingStatus.Confirmed, await d.GetAsync(db => db.BabysittingBookings.Where(b => b.Id == d.BookingId).Select(b => b.Status).SingleAsync()));
        Assert.Equal(40m, await d.GetAsync(db => db.Payments.Where(p => p.BookingId == d.BookingId && p.Status == PaymentStatus.Succeeded).SumAsync(p => p.Amount)));
        Assert.Equal(1, await d.GetAsync(db => db.Notifications.CountAsync(n => n.UserId == d.Sitter.Id)));
    }

    [SqlServerFact]
    public async Task Parents_review_only_completed_bookings_once()
    {
        await using var upcoming = await Data.CreateAsync(booked: true);
        Assert.False((await upcoming.GetAsync(db => new ParentBookingService(db, TimeProvider.System).ReviewAsync(upcoming.Parent.Id, upcoming.BookingId, 5, "Great"))).Succeeded);
        await using var d = await Data.CreateAsync(booked: true, BookingStatus.Completed);
        Assert.False((await d.GetAsync(db => new ParentBookingService(db, TimeProvider.System).ReviewAsync(d.Parent.Id, d.BookingId, 6, null))).Succeeded);
        Assert.True((await d.GetAsync(db => new ParentBookingService(db, TimeProvider.System).ReviewAsync(d.Stranger.Id, d.BookingId, 5, null))).NotFound);
        Assert.True((await d.GetAsync(db => new ParentBookingService(db, TimeProvider.System).ReviewAsync(d.Parent.Id, d.BookingId, 5, "Lovely evening"))).Succeeded);
        Assert.False((await d.GetAsync(db => new ParentBookingService(db, TimeProvider.System).ReviewAsync(d.Parent.Id, d.BookingId, 4, "Again"))).Succeeded);
        Assert.Equal(5, await d.GetAsync(db => db.Reviews.Where(r => r.BookingId == d.BookingId).Select(r => r.Rating).SingleAsync()));
    }

    [SqlServerFact]
    public async Task Notifications_are_marked_read_only_for_their_owner()
    {
        await using var d = await Data.CreateAsync(booked: false);
        var id = Guid.NewGuid();
        await d.UseAsync(async db => { db.Notifications.Add(new Notification { Id = id, UserId = d.Parent.Id, Title = "Hi", Body = "Test" }); await db.SaveChangesAsync(); });
        Assert.False(await d.GetAsync(db => new ParentBookingService(db, TimeProvider.System).MarkReadAsync(d.Stranger.Id, id)));
        Assert.Single(await d.GetAsync(db => new ParentBookingService(db, TimeProvider.System).NotificationsAsync(d.Parent.Id)));
        Assert.True(await d.GetAsync(db => new ParentBookingService(db, TimeProvider.System).MarkReadAsync(d.Parent.Id, id)));
        Assert.NotNull(await d.GetAsync(db => db.Notifications.Where(n => n.Id == id).Select(n => n.ReadAtUtc).SingleAsync()));
    }

    [SqlServerFact]
    public async Task Care_in_progress_and_stale_pages_cannot_cancel()
    {
        await using var started = await Data.CreateAsync(booked: true, BookingStatus.InProgress);
        Assert.False((await Cancel(started, started.Parent.Id)).Succeeded);
        await using var stale = await Data.CreateAsync(booked: true);
        Assert.False((await Cancel(stale, stale.Parent.Id, Convert.ToBase64String(new byte[8]))).Succeeded);
        Assert.Equal(BookingStatus.Confirmed, await stale.GetAsync(db => db.BabysittingBookings.Where(b => b.Id == stale.BookingId).Select(b => b.Status).SingleAsync()));
    }
}
