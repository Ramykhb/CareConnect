using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BabySitting.Models;
using BabySitting.Services.Requests;
using BabySitting.ViewModels;
using Xunit;

namespace BabySitting.Tests;

public sealed class SitterRequestsTests
{
    private static readonly Guid RequestId = Guid.Parse("06568ee8-bf1e-471d-b8f8-66091a4f6eb5");
    private sealed class InboxFixture(bool empty = false) : ISitterRequestService
    {
        public string? Decision { get; private set; }
        public Task<SitterRequestsViewModel> LoadAsync(ApplicationUser account, CancellationToken ct = default)
        {
            var now = new DateTimeOffset(2026, 11, 12, 8, 24, 0, TimeSpan.Zero);
            account.FirstName = "Omar";
            var states = new[] { Decision == "accept" ? IncomingRequestState.Accepted : Decision == "decline" ? IncomingRequestState.Declined : IncomingRequestState.New, IncomingRequestState.Pending, IncomingRequestState.Accepted, IncomingRequestState.Declined, IncomingRequestState.Expired };
            var items = states.Select((state, i) => new IncomingRequestViewModel
            {
                Id = i == 0 ? RequestId : Guid.Parse($"10000000-0000-0000-0000-{i:000000000000}"), Version = Convert.ToBase64String(new byte[16]),
                OwnerName = i == 0 ? "Sarah Haddad" : "Nour Hamdan", OwnerFirstName = i == 0 ? "Sarah" : "Nour", OwnerSince = 2024,
                Title = "Evening care", ServiceType = ServiceType.Babysitting, City = "Beirut", Note = "Please leave the radio on low at night.", Amount = 135,
                StartsAtUtc = now.AddDays(8), EndsAtUtc = now.AddDays(10), CreatedAtUtc = now.AddMinutes(-i * 10), State = state,
                CanAccept = state is IncomingRequestState.New or IncomingRequestState.Pending, CanDecline = state is IncomingRequestState.New or IncomingRequestState.Pending,
                BookingId = state == IncomingRequestState.Accepted ? Guid.Parse("c9d5ec83-3a55-46a9-9329-d3ca7da1a1ac") : null,
                BookingStatus = state == IncomingRequestState.Accepted ? BookingStatus.AwaitingPayment : null, AcceptedAtUtc = state == IncomingRequestState.Accepted ? now : null,
                Address = state == IncomingRequestState.Accepted ? "Accepted address" : null,
                Children = new(2, 3, 7, "Peanut allergy.", "Bath at 7, bed at 8.", "Story before bed."),
                FitChecks = [new("No conflicting bookings for the requested dates", true), new("In your service city · Beirut", true)]
            }).ToArray();
            return Task.FromResult(new SitterRequestsViewModel { Account = account, Now = now, Items = empty ? [] : items, Profile = new() { City = "Beirut", IsAvailable = true } });
        }
        public Task<RequestDecisionResult> DecideAsync(ApplicationUser account, RequestDecisionForm form, CancellationToken ct = default)
        {
            Decision = form.Decision;
            return Task.FromResult(new RequestDecisionResult(true, "Decision sent — Sarah was notified.", form.Decision == "accept" ? "accepted" : "declined"));
        }
    }

    private static WebApplicationFactory<Program> Factory(InboxFixture? fixture = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development").ConfigureServices(services =>
    {
        services.RemoveAll<IUserStore<ApplicationUser>>(); services.AddScoped<IUserStore<ApplicationUser>, DashboardTests.DashboardUsers>();
        services.RemoveAll<ISitterRequestService>(); services.AddSingleton<ISitterRequestService>(fixture ?? new InboxFixture());
        services.AddAuthentication(options => options.DefaultAuthenticateScheme = "RequestTests").AddScheme<AuthenticationSchemeOptions, DashboardTests.DashboardAuthentication>("RequestTests", _ => { });
    }));

    [Theory]
    [InlineData("/SitterRequests", null)]
    [InlineData("/SitterRequests/Updates", null)]
    [InlineData("/SitterRequests", Roles.Customer)]
    [InlineData("/SitterRequests/Updates", Roles.Customer)]
    public async Task Inbox_requires_the_sitter_role(string path, string? role)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", role);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(role is null ? "Login" : "AccessDenied", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("new", "Accept request")]
    [InlineData("pending", "Pending decision")]
    [InlineData("accepted", "Open booking")]
    [InlineData("declined", "You declined this request")]
    [InlineData("expired", "This request is no longer available")]
    public async Task Inbox_renders_each_status_and_private_care_details(string status, string expected)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", Roles.Sitter);
        using var response = await client.GetAsync("/SitterRequests?status=" + status);
        var html = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("Incoming requests", html);
        Assert.Contains(expected, html);
        Assert.Contains("Peanut allergy", html);
        Assert.Contains("__RequestVerificationToken", html);
        if (status == "accepted") Assert.Contains("Accepted address", html);
        else Assert.DoesNotContain("Accepted address", html);
    }

    [Fact]
    public async Task An_empty_inbox_renders_a_useful_starting_state()
    {
        await using var factory = Factory(new InboxFixture(empty: true));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", Roles.Sitter);
        var html = await client.GetStringAsync("/SitterRequests");
        Assert.Contains("No requests here yet", html);
        Assert.Contains("Ready for your next family to help", html);
        Assert.Contains("all caught up", html);
    }

    [Fact]
    public async Task Selection_search_confirmation_and_updates_work_without_a_database()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", Roles.Sitter);
        var html = await client.GetStringAsync($"/SitterRequests?id={RequestId}&confirm=accept");
        Assert.Contains("Accept this request?", html);
        Assert.Contains("data-auto-confirm=\"accept\"", html);
        var update = await client.GetStringAsync("/SitterRequests/Updates?status=new&search=EVENING");
        Assert.Contains("data-inbox-fragment", update);
        Assert.Contains("Sarah Haddad", update);
        Assert.DoesNotContain("Accepted address", update);
        var empty = await client.GetStringAsync("/SitterRequests/Updates?search=unmatched");
        Assert.Contains("No matching requests", empty);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/SitterRequests?id=" + Guid.NewGuid())).StatusCode);
    }

    [Theory]
    [InlineData("accept", "accepted", "Open booking")]
    [InlineData("decline", "declined", "You declined this request")]
    public async Task Decision_requires_CSRF_and_redirects_to_the_saved_state(string decision, string status, string expected)
    {
        var fixture = new InboxFixture();
        await using var factory = Factory(fixture);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", Roles.Sitter);
        var fields = new Dictionary<string, string> { ["RequestId"] = RequestId.ToString(), ["Version"] = Convert.ToBase64String(new byte[16]), ["Decision"] = decision };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/SitterRequests/Decide", new FormUrlEncodedContent(fields))).StatusCode);
        Assert.Null(fixture.Decision);
        var html = await client.GetStringAsync("/SitterRequests");
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(fields["__RequestVerificationToken"]);
        using var response = await client.PostAsync("/SitterRequests/Decide", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("status=" + status, response.Headers.Location!.ToString());
        Assert.Equal(decision, fixture.Decision);
        var saved = await client.GetStringAsync(response.Headers.Location);
        Assert.Contains("Decision sent", saved);
        Assert.Contains(expected, saved);
    }

    [Fact]
    public void Availability_checks_merge_adjacent_windows_and_exclude_cancelled_bookings()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var request = new ChildcareRequest { StartsAtUtc = start, EndsAtUtc = start.AddHours(2) };
        var slots = new[] { new SitterAvailability { StartsAtUtc = start, EndsAtUtc = start.AddHours(1) }, new SitterAvailability { StartsAtUtc = start.AddHours(1), EndsAtUtc = start.AddHours(2) } };
        Assert.True(SitterRequestPolicy.Covered(request, slots));
        slots[1].StartsAtUtc = start.AddMinutes(61);
        Assert.False(SitterRequestPolicy.Covered(request, slots));
        Assert.False(SitterRequestPolicy.Overlaps(request, [new() { StartsAtUtc = start, EndsAtUtc = start.AddHours(3), Status = BookingStatus.Cancelled }]));
        Assert.False(SitterRequestPolicy.Overlaps(request, [new() { StartsAtUtc = start.AddHours(-1), EndsAtUtc = start, Status = BookingStatus.Confirmed }]));
        Assert.True(SitterRequestPolicy.Overlaps(request, [new() { StartsAtUtc = start.AddHours(1), EndsAtUtc = start.AddHours(3), Status = BookingStatus.Confirmed }]));
    }
}
