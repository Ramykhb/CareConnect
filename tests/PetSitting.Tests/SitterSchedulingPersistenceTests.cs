using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Bookings;
using PetSitting.Services.Scheduling;
using PetSitting.ViewModels;
using Xunit;
using Scenario = PetSitting.Tests.SitterRequestPersistenceTests.Scenario;

namespace PetSitting.Tests;

public sealed class SitterSchedulingPersistenceTests
{
    private static async Task<T> Booking<T>(Scenario s, Func<ISitterBookingService, Task<T>> action)
    { using var scope = s.Factory.Services.CreateScope(); return await action(scope.ServiceProvider.GetRequiredService<ISitterBookingService>()); }
    private static async Task<T> Schedule<T>(Scenario s, Func<ISitterAvailabilityService, Task<T>> action)
    { using var scope = s.Factory.Services.CreateScope(); return await action(scope.ServiceProvider.GetRequiredService<ISitterAvailabilityService>()); }
    private static async Task<Guid> Accepted(Scenario s, bool paid = false)
    {
        var result = await s.DecideAsync(await s.FormAsync()); Assert.True(result.Succeeded, result.Message);
        await s.ChangeAsync(async db =>
        {
            var b = await db.PetBookings.Include(b => b.Request).SingleAsync(b => b.Id == result.BookingId);
            b.StartsAtUtc = b.Request.StartsAtUtc = DateTimeOffset.UtcNow.AddHours(-1);
            b.EndsAtUtc = b.Request.EndsAtUtc = DateTimeOffset.UtcNow.AddHours(2);
            if (paid) db.Payments.Add(new() { BookingId = b.Id, PayerId = s.Owner.Id, Amount = b.TotalAmount, Status = PaymentStatus.Succeeded });
        });
        return result.BookingId!.Value;
    }
    private static async Task<BookingTransitionForm> Transition(Scenario s, Guid id, string action) => new() { Id = id, Action = action, Version = Convert.ToBase64String((await Booking(s, b => b.LoadAsync(s.Sitter, id))).Booking!.RowVersion) };
    private static async Task<AvailabilityEditForm> Edit(Scenario s, DateOnly day, bool available = false) => (await Schedule(s, a => a.LoadAsync(s.Sitter, day, "month"))).Editor is { } editor ? new() { StartDate = day, EndDate = day, Version = editor.Version, Available = available } : throw new InvalidOperationException();

    [SqlServerFact]
    public async Task Care_status_requires_full_payment_and_saves_timeline_and_notifications_once()
    {
        await using var s = await Scenario.CreateAsync(); var id = await Accepted(s);
        var start = await Transition(s, id, "start");
        Assert.False((await Booking(s, b => b.TransitionAsync(s.Sitter.Id, start))).Succeeded);
        await s.ChangeAsync(db => { db.Payments.Add(new() { BookingId = id, PayerId = s.Owner.Id, Amount = 135, Currency = "EUR", Status = PaymentStatus.Succeeded }); return Task.CompletedTask; });
        Assert.False((await Booking(s, b => b.TransitionAsync(s.Sitter.Id, start))).Succeeded);
        await s.ChangeAsync(db => { db.Payments.Add(new() { BookingId = id, PayerId = s.Owner.Id, Amount = 135, Status = PaymentStatus.Succeeded }); return Task.CompletedTask; });
        Assert.True((await Booking(s, b => b.TransitionAsync(s.Sitter.Id, start))).Succeeded);
        Assert.True((await Booking(s, b => b.TransitionAsync(s.Sitter.Id, start))).Succeeded);
        var complete = await Transition(s, id, "complete");
        var results = await Task.WhenAll(Booking(s, b => b.TransitionAsync(s.Sitter.Id, complete)), Booking(s, b => b.TransitionAsync(s.Sitter.Id, complete)));
        Assert.All(results, r => Assert.True(r.Succeeded, r.Message));
        var saved = (await Booking(s, b => b.LoadAsync(s.Sitter, id))).Booking!;
        Assert.Equal(BookingStatus.Completed, saved.Status); Assert.NotNull(saved.StartedAtUtc); Assert.NotNull(saved.CompletedAtUtc);
        Assert.Equal(RequestStatus.Completed, saved.Request.Status);
        Assert.Equal(3, await s.QueryAsync(db => db.Notifications.CountAsync(n => n.UserId == s.Owner.Id)));
        Assert.False((await Booking(s, async b => await b.TransitionAsync(s.Sitter.Id, await Transition(s, id, "start")))).Succeeded);
    }

    [SqlServerFact]
    public async Task Checklist_items_are_owned_persisted_and_locked_after_completion()
    {
        await using var s = await Scenario.CreateAsync(); var id = await Accepted(s, true);
        Assert.True((await Booking(s, async b => await b.TransitionAsync(s.Sitter.Id, await Transition(s, id, "start")))).Succeeded);
        var model = await Booking(s, b => b.LoadAsync(s.Sitter, id)); var key = BookingRules.Tasks(model.Booking!).First().Key;
        Assert.True((await Booking(s, b => b.TaskAsync(s.Sitter.Id, new() { Id = id, Key = key, Completed = true }))).Succeeded);
        Assert.True((await Booking(s, b => b.TaskAsync(s.Sitter.Id, new() { Id = id, Key = key, Completed = true }))).Succeeded);
        Assert.Equal(1, await s.QueryAsync(db => db.BookingCareTasks.CountAsync(t => t.BookingId == id)));
        Assert.False((await Booking(s, b => b.TaskAsync(s.OtherSitter.Id, new() { Id = id, Key = key, Completed = false }))).Succeeded);
        Assert.True((await Booking(s, b => b.AddTaskAsync(s.Sitter.Id, new() { Id = id, Label = "Refresh water" }))).Succeeded);
        Assert.Equal(4, BookingRules.Tasks((await Booking(s, b => b.LoadAsync(s.Sitter, id))).Booking!).Count());
        Assert.True((await Booking(s, async b => await b.TransitionAsync(s.Sitter.Id, await Transition(s, id, "complete")))).Succeeded);
        Assert.False((await Booking(s, b => b.TaskAsync(s.Sitter.Id, new() { Id = id, Key = key, Completed = false }))).Succeeded);
        Assert.Null((await Booking(s, b => b.LoadAsync(s.OtherSitter, id))).Booking);
    }

    [SqlServerFact]
    public async Task Photo_updates_notify_the_owner_and_images_are_private_and_idempotent()
    {
        await using var s = await Scenario.CreateAsync(); var id = await Accepted(s, true);
        Assert.True((await Booking(s, async b => await b.TransitionAsync(s.Sitter.Id, await Transition(s, id, "start")))).Succeeded);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jPioAAAAASUVORK5CYII=");
        using var stream = new MemoryStream(png);
        var form = new BookingUpdateForm { Id = id, ClientToken = Guid.NewGuid(), Message = "Max enjoyed his walk.", Photo = new FormFile(stream, 0, png.Length, "Photo", "max.png") };
        Assert.True((await Booking(s, b => b.UpdateAsync(s.Sitter.Id, form, false))).Succeeded);
        stream.Position = 0;
        Assert.True((await Booking(s, b => b.UpdateAsync(s.Sitter.Id, form, false))).Succeeded);
        var update = await s.QueryAsync(db => db.BookingUpdates.SingleAsync(u => u.BookingId == id));
        Assert.True(update.HasPhoto);
        Assert.NotNull(await Booking(s, b => b.PhotoAsync(s.Owner.Id, update.Id)));
        Assert.NotNull(await Booking(s, b => b.PhotoAsync(s.Sitter.Id, update.Id)));
        Assert.Null(await Booking(s, b => b.PhotoAsync(s.OtherSitter.Id, update.Id)));
        Assert.Equal(3, await s.QueryAsync(db => db.Notifications.CountAsync(n => n.UserId == s.Owner.Id)));
        using var invalid = new MemoryStream("<svg>not an image</svg>"u8.ToArray());
        var bad = new BookingUpdateForm { Id = id, ClientToken = Guid.NewGuid(), Photo = new FormFile(invalid, 0, invalid.Length, "Photo", "bad.svg") };
        Assert.False((await Booking(s, b => b.UpdateAsync(s.Sitter.Id, bad, false))).Succeeded);
        Assert.Equal(1, await s.QueryAsync(db => db.BookingUpdates.CountAsync(u => u.BookingId == id)));
    }

    [SqlServerFact]
    public async Task Availability_blocks_acceptance_and_an_available_override_restores_the_selected_day()
    {
        await using var s = await Scenario.CreateAsync(); var day = ScheduleMath.LocalDate(s.Requests[0].StartsAtUtc);
        await s.ChangeAsync(async db => { var request = await db.PetCareRequests.SingleAsync(r => r.Id == s.Requests[0].Id); request.StartsAtUtc = ScheduleMath.Utc(day, new(10, 0)); request.EndsAtUtc = ScheduleMath.Utc(day, new(12, 0)); });
        var form = await Edit(s, day); form.ApplyRange = true; form.EndDate = day.AddDays(2); form.Reason = "PRIVATE family trip"; form.RepeatYearly = true;
        Assert.True((await Schedule(s, a => a.SaveAsync(s.Sitter.Id, form))).Succeeded);
        Assert.False((await s.ReadAsync()).Items.Single(i => i.Id == s.Requests[0].Id).CanAccept);
        Assert.False((await s.DecideAsync(await s.FormAsync())).Succeeded);
        Assert.False((await Schedule(s, a => a.SaveAsync(s.Sitter.Id, form))).Succeeded); // Stale schedule version.
        var open = await Edit(s, day, true);
        Assert.True((await Schedule(s, a => a.SaveAsync(s.Sitter.Id, open))).Succeeded);
        Assert.True((await s.ReadAsync()).Items.Single(i => i.Id == s.Requests[0].Id).CanAccept);
        var calendar = await Schedule(s, a => a.LoadAsync(s.Sitter, day, "month"));
        Assert.NotEmpty(calendar.Available(day)); Assert.Empty(calendar.Available(day.AddDays(1)));
        Assert.True((await s.DecideAsync(await s.FormAsync())).Succeeded);
    }

    [SqlServerFact]
    public async Task Blocking_dates_with_an_active_booking_is_rejected_without_changing_the_schedule()
    {
        await using var s = await Scenario.CreateAsync(); var accepted = await s.DecideAsync(await s.FormAsync()); Assert.True(accepted.Succeeded);
        var day = ScheduleMath.LocalDate(s.Requests[0].StartsAtUtc); var form = await Edit(s, day);
        Assert.Equal(1, await Schedule(s, a => a.AffectedAsync(s.Sitter.Id, form)));
        Assert.False((await Schedule(s, a => a.SaveAsync(s.Sitter.Id, form))).Succeeded);
        Assert.Equal(0, await s.QueryAsync(db => db.SitterScheduleExceptions.CountAsync(e => e.SitterProfileId == s.ProfileId)));
        Assert.Equal(BookingStatus.AwaitingPayment, await s.QueryAsync(db => db.PetBookings.Where(b => b.Id == accepted.BookingId).Select(b => b.Status).SingleAsync()));
    }

    [SqlServerFact]
    public async Task A_concurrent_date_block_and_request_acceptance_cannot_both_succeed()
    {
        await using var s = await Scenario.CreateAsync(); var day = ScheduleMath.LocalDate(s.Requests[0].StartsAtUtc);
        var block = await Edit(s, day); var accept = await s.FormAsync();
        var blockTask = Schedule(s, a => a.SaveAsync(s.Sitter.Id, block)); var acceptTask = s.DecideAsync(accept);
        await Task.WhenAll(blockTask, acceptTask);
        Assert.NotEqual(blockTask.Result.Succeeded, acceptTask.Result.Succeeded);
    }

    [Fact]
    public void Yearly_ranges_respect_local_dates_and_newer_overrides()
    {
        var rule = new SitterScheduleException { StartsOn = new(2026, 11, 27), EndsOn = new(2026, 11, 29), RepeatYearly = true, IsAvailable = false, CreatedAtUtc = DateTimeOffset.UtcNow };
        var start = ScheduleMath.Utc(new(2027, 11, 27)); var end = ScheduleMath.Utc(new(2027, 11, 30));
        Assert.False(ScheduleMath.Covered([], [rule], start, end));
        var available = new SitterScheduleException { StartsOn = new(2027, 11, 28), EndsOn = new(2027, 11, 28), IsAvailable = true, CreatedAtUtc = rule.CreatedAtUtc.AddMinutes(1) };
        Assert.True(ScheduleMath.Covered([], [rule, available], ScheduleMath.Utc(new(2027, 11, 28)), ScheduleMath.Utc(new(2027, 11, 29))));
        Assert.False(ScheduleMath.Covered([], [rule, available], start, end));
        Assert.Equal(6, new SitterAvailabilityViewModel { SelectedDate = new(2026, 11, 1) }.MonthCellCount / 7);
    }
}
