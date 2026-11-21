using System.Net;
using System.Text.RegularExpressions;
using CareConnect.Ai;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PetSitting.Models;
using Xunit;

namespace PetSitting.Tests;

[Route("__tests/care-summary")]
public sealed class CareSummaryPreviewController : Controller
{
    [HttpGet] public IActionResult Preview() => PartialView("/Views/Shared/_CareSummary.cshtml", CareSummaryPageTests.BookingId);
}
public sealed class CareSummaryPageTests
{
    public static readonly Guid BookingId = Guid.NewGuid();
    private sealed class Summaries : ICareSummaryService
    {
        public Task<CareSummaryResult?> GenerateAsync(Guid user, Guid booking, DateOnly? date, CancellationToken ct = default) =>
            Task.FromResult<CareSummaryResult?>(booking != BookingId ? null : new("ready", "Recorded care summary", [new("A task was marked complete.", ["s1"])], [new("s1", "task", "Activity", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, false)], DateTimeOffset.UtcNow, date, 1, 0, 0, 0));
    }
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development").ConfigureServices(s =>
    {
        s.RemoveAll<IHostedService>(); s.RemoveAll<ICareSummaryService>(); s.AddSingleton<ICareSummaryService, Summaries>();
        s.AddControllersWithViews().AddApplicationPart(typeof(CareSummaryPreviewController).Assembly);
        s.AddAuthentication(o => o.DefaultAuthenticateScheme = "CareSummaryTests").AddScheme<AuthenticationSchemeOptions, DashboardTests.DashboardAuthentication>("CareSummaryTests", _ => { });
    }));
    [Theory]
    [InlineData(null)]
    [InlineData(Roles.Sitter)]
    public async Task Summaries_require_the_customer_role(string? role)
    {
        await using var factory = Factory(); using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        if (role != null) client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", role);
        using var response = await client.PostAsync("/CareSummaries/Generate", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
    [Fact]
    public async Task Summary_POST_requires_antiforgery()
    {
        await using var factory = Factory(); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", Roles.Customer);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/CareSummaries/Generate", new FormUrlEncodedContent([]))).StatusCode);
    }
    [Fact]
    public async Task Controls_and_authorized_summary_response_work_with_the_existing_booking_UI()
    {
        await using var factory = Factory(); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", Roles.Customer);
        var html = await client.GetStringAsync("/__tests/care-summary");
        Assert.Contains("Summarize care", html); Assert.Contains("Entire booking", html); Assert.Contains("Beirut", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        var fields = new Dictionary<string,string> { ["__RequestVerificationToken"] = token, ["bookingId"] = BookingId.ToString(), ["scope"] = "day", ["date"] = "2026-11-20" };
        using var response = await client.PostAsync("/CareSummaries/Generate", new FormUrlEncodedContent(fields));
        response.EnsureSuccessStatusCode(); Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadAsStringAsync(); Assert.Contains("sourceKeys", body); Assert.Contains("marked complete", body);
        fields["bookingId"] = Guid.NewGuid().ToString(); Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/CareSummaries/Generate", new FormUrlEncodedContent(fields))).StatusCode);
        fields["bookingId"] = BookingId.ToString(); fields["date"] = "not-a-date"; Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/CareSummaries/Generate", new FormUrlEncodedContent(fields))).StatusCode);
    }
}
