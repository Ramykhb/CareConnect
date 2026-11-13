using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BabySitting.Models;
using BabySitting.Services.Bookings;
using BabySitting.Services.Scheduling;
using BabySitting.ViewModels;
using Xunit;

namespace BabySitting.Tests;

public sealed class SitterSchedulingPageTests
{
    private static readonly Guid BookingId = Guid.Parse("74b9abb0-ac6f-438c-a073-64955081c433");
    private sealed class Pages : ISitterBookingService, ISitterAvailabilityService
    {
        private static DateTimeOffset Now => new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        private static BabysitterProfile Profile => new() { City = "Beirut", RowVersion = new byte[8] };
        private static BabysittingBooking Booking => new()
        {
            Id = BookingId, RowVersion = new byte[8], StartsAtUtc = Now.AddHours(-1), EndsAtUtc = Now.AddDays(2), CreatedAtUtc = Now.AddDays(-1), StartedAtUtc = Now.AddHours(-1), Status = BookingStatus.InProgress, TotalAmount = 135,
            Customer = new() { FirstName = "+Sarah", LastName = "Haddad", PhoneNumber = "12345678" },
            Request = new() { Title = "Evening care", ServiceType = ServiceType.Babysitting, City = "Beirut", ServiceAddress = "Shared booking address", NumberOfChildren = 2, YoungestChildAgeYears = 3, OldestChildAgeYears = 7, CareInstructions = "Leave the radio on low.", FamilyProfile = new() { City = "Beirut", Routines = "One story before bed." } },
            Payments = [new() { Status = PaymentStatus.Succeeded, Amount = 135 }]
        };
        public Task<SitterBookingsViewModel> LoadAsync(ApplicationUser account, Guid? id = null, CancellationToken ct = default) => Task.FromResult(new SitterBookingsViewModel { Account = account, Profile = Profile, Now = Now, Bookings = [Booking], Booking = id == BookingId ? Booking : null });
        public Task<SitterAvailabilityViewModel> LoadAsync(ApplicationUser account, DateOnly? date, string? view, CancellationToken ct = default)
        {
            var selected = date ?? new DateOnly(2026, 11, 13);
            var eventStart = view == "week" ? ScheduleMath.Utc(ScheduleMath.Monday(selected).AddDays(2), new(9, 0)) : Now.AddHours(-1);
            return Task.FromResult(new SitterAvailabilityViewModel { Account = account, Profile = Profile, Now = Now, SelectedDate = selected, View = view == "week" ? "week" : "month", Events = [new(BookingId, "Max · Overnight care", "Sarah H.", eventStart, eventStart.AddHours(2), "in-progress")], Editor = new() { StartDate = selected, EndDate = selected, Version = Convert.ToBase64String(new byte[8]), View = view ?? "month" } });
        }
        public Task<BookingWriteResult> TransitionAsync(Guid userId, BookingTransitionForm form, CancellationToken ct = default) => Task.FromResult(new BookingWriteResult(true, "Saved"));
        public Task<BookingWriteResult> TaskAsync(Guid userId, CareTaskForm form, CancellationToken ct = default) => Task.FromResult(new BookingWriteResult(true, "Saved"));
        public Task<BookingWriteResult> AddTaskAsync(Guid userId, AddCareTaskForm form, CancellationToken ct = default) => Task.FromResult(new BookingWriteResult(true, "Saved"));
        public Task<BookingWriteResult> UpdateAsync(Guid userId, BookingUpdateForm form, bool issue, CancellationToken ct = default) => Task.FromResult(new BookingWriteResult(true, "Saved"));
        public Task<BookingUpdatePhoto?> PhotoAsync(Guid userId, Guid updateId, CancellationToken ct = default) => Task.FromResult<BookingUpdatePhoto?>(null);
        public Task<ScheduleWriteResult> SaveAsync(Guid userId, AvailabilityEditForm form, CancellationToken ct = default) => Task.FromResult(new ScheduleWriteResult(true, "Saved"));
        public Task<ScheduleWriteResult> ToggleAsync(Guid userId, bool accepting, string version, CancellationToken ct = default) => Task.FromResult(new ScheduleWriteResult(true, "Saved"));
        public Task<int> AffectedAsync(Guid userId, AvailabilityEditForm form, CancellationToken ct = default) => Task.FromResult(0);
    }
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development").ConfigureServices(services =>
    {
        services.RemoveAll<IUserStore<ApplicationUser>>(); services.AddScoped<IUserStore<ApplicationUser>, DashboardTests.DashboardUsers>();
        services.RemoveAll<ISitterBookingService>(); services.AddSingleton<ISitterBookingService, Pages>();
        services.RemoveAll<ISitterAvailabilityService>(); services.AddSingleton<ISitterAvailabilityService, Pages>();
        services.AddAuthentication(options => options.DefaultAuthenticateScheme = "SchedulingTests").AddScheme<AuthenticationSchemeOptions, DashboardTests.DashboardAuthentication>("SchedulingTests", _ => { });
    }));
    [Theory]
    [InlineData("/SitterBookings", "Bookings", "This week")]
    [InlineData("/SitterBookings/Details/74b9abb0-ac6f-438c-a073-64955081c433", "Care checklist", "Send a photo update")]
    [InlineData("/SitterAvailability?date=2026-11-27&view=month", "November 2026", "Repeat yearly")]
    [InlineData("/SitterAvailability?date=2026-11-20&view=week", "16–22 Nov 2026", "Available hours")]
    public async Task All_four_scheduling_views_render(string path, string first, string second)
    {
        await using var factory = Factory(); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", Roles.Sitter);
        using var response = await client.GetAsync(path); var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        response.EnsureSuccessStatusCode(); Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains(first, html); Assert.Contains(second, html); Assert.Contains("__RequestVerificationToken", html);
    }
    [Theory]
    [InlineData("/SitterBookings", null)]
    [InlineData("/SitterAvailability", null)]
    [InlineData("/SitterBookings", Roles.Customer)]
    [InlineData("/SitterAvailability", Roles.Customer)]
    [InlineData("/SitterAvailability/Preview", Roles.Customer)]
    public async Task Scheduling_pages_require_the_sitter_role(string path, string? role)
    {
        await using var factory = Factory(); using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", role);
        using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.Contains(role is null ? "Login" : "AccessDenied", response.Headers.Location!.ToString());
    }
    [Theory]
    [InlineData("/SitterBookings/Transition")]
    [InlineData("/SitterBookings/Task")]
    [InlineData("/SitterBookings/AddTask")]
    [InlineData("/SitterBookings/SendUpdate")]
    [InlineData("/SitterBookings/ReportIssue")]
    [InlineData("/SitterAvailability/Save")]
    [InlineData("/SitterAvailability/Toggle")]
    public async Task Scheduling_writes_require_antiforgery(string path)
    {
        await using var factory = Factory(); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", Roles.Sitter);
        using var response = await client.PostAsync(path, new FormUrlEncodedContent([])); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact]
    public async Task Export_escapes_formula_fields_and_unknown_bookings_return_not_found()
    {
        await using var factory = Factory(); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", Roles.Sitter);
        var csv = await client.GetStringAsync("/SitterBookings/Export"); Assert.Contains("\"'+Sarah Haddad\"", csv);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/SitterBookings/Details/" + Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/BookingPhotos/Image/" + Guid.NewGuid())).StatusCode);
    }
}
