using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Models;
using PetSitting.ViewModels;
using Xunit;

namespace PetSitting.Tests;

// This controller is only added to MVC by the test factory, to exercise real Razor output without SQL.
[Route("__tests/matching")]
public sealed class MatchingPreviewController : Controller
{
    public static readonly Guid SitterId = Guid.Parse("ad3ac1a0-4acf-4940-a43e-2b23b61735c3");
    [HttpGet]
    public IActionResult Preview(bool ai = false, bool requested = false, bool wizard = false)
    {
        var model = new OwnerRequestWizard
        {
            Step = 5, MatchedByAi = ai, MatchingRequested = requested,
            Form = new() { PreferredSitterId = SitterId, Budget = 60 },
            Sitters = [new PetSitterProfile { Id = SitterId, City = "Beirut", YearsOfExperience = 5,
                User = new() { FirstName = "Alex", LastName = "Sitter" } }],
            MatchReasons = new Dictionary<Guid, string> { [SitterId] = "Experienced <script>alert(1)</script>" }
        };
        return PartialView(wizard ? "/Views/CareRequests/Create.cshtml" : "/Views/CareRequests/_SitterMatches.cshtml", model);
    }
}

public sealed class AiMatchingPageTests
{
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.UseEnvironment("Development").ConfigureServices(services =>
        {
            services.AddControllersWithViews().AddApplicationPart(typeof(MatchingPreviewController).Assembly);
            services.AddAuthentication(options => options.DefaultAuthenticateScheme = "MatchingTests")
                .AddScheme<AuthenticationSchemeOptions, DashboardTests.DashboardAuthentication>("MatchingTests", _ => { });
        }));

    [Fact]
    public async Task Recommendations_render_AI_status_encode_reasons_and_keep_the_preference()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/__tests/matching?ai=true&requested=true");
        Assert.Contains("AI recommendations by Groq", html);
        Assert.Contains("Best match", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("checked=\"checked\"", html);
        Assert.Contains(MatchingPreviewController.SitterId.ToString(), html);
    }

    [Fact]
    public async Task Step_five_exposes_matching_with_a_non_JavaScript_form_action()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/__tests/matching?wizard=true");
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, html);
        Assert.Contains("Match with AI", html);
        Assert.Contains("formaction=\"/CareRequests/Matches\"", html);
        Assert.Contains("data-sitter-matches", html);
        Assert.Contains("__RequestVerificationToken", html);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Roles.Sitter)]
    public async Task Matching_requires_the_customer_role(string? role)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", role);
        using var response = await client.PostAsync("/CareRequests/Matches", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(role is null ? "Login" : "AccessDenied", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Matching_requires_antiforgery()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", Roles.Customer);
        using var response = await client.PostAsync("/CareRequests/Matches", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
