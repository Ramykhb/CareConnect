using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Auth;
using BabySitting.Services.Scheduling;
using BabySitting.ViewModels;
using Xunit;

namespace BabySitting.Tests;

public sealed class ParentRequestTests
{
    [Theory]
    [InlineData("/CareRequests/Create")]
    [InlineData("/CareRequests/Submitted/00000000-0000-0000-0000-000000000001")]
    [InlineData("/CareRequests/Status/00000000-0000-0000-0000-000000000001")]
    public async Task Request_flow_requires_an_authenticated_owner(string path)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Identity/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public void Request_dates_are_converted_from_Beirut_time()
    {
        var form = new ParentRequestForm { StartDate = new(2027, 1, 15), EndDate = new(2027, 1, 16), StartTime = new(18, 0), EndTime = new(10, 0) };
        var times = form.Times()!.Value;
        Assert.Equal(new DateTimeOffset(2027, 1, 15, 16, 0, 0, TimeSpan.Zero), times.Start);
        Assert.Equal(new DateTimeOffset(2027, 1, 16, 8, 0, 0, TimeSpan.Zero), times.End);
    }

    [SqlServerFact]
    public async Task Six_step_flow_saves_drafts_validates_ownership_and_submits_once()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var otherClient = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var sitterClient = factory.CreateClient(new() { AllowAutoRedirect = false });
        var suffix = Guid.NewGuid().ToString("N");
        var ownerEmail = $"request-owner-{suffix}@example.test";
        var otherEmail = $"request-other-{suffix}@example.test";
        var sitterEmail = $"request-sitter-{suffix}@example.test";
        var city = "WizardCity-" + suffix;
        var ownerId = Guid.Empty; var otherId = Guid.Empty; var sitterId = Guid.Empty;
        var profileId = Guid.NewGuid();
        var draftId = Guid.Empty;
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var registration = scope.ServiceProvider.GetRequiredService<RegistrationService>();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                foreach (var (email, role) in new[] { (ownerEmail, "Customer"), (otherEmail, "Customer"), (sitterEmail, "Sitter") })
                {
                    var (result, user) = await registration.RegisterAsync(email, "Test!Password123", "Wizard", "Test", role);
                    Assert.True(result.Succeeded);
                    user!.EmailConfirmed = true; user.City = city;
                    Assert.True((await users.UpdateAsync(user)).Succeeded);
                    if (email == ownerEmail) ownerId = user.Id;
                    else if (email == otherEmail) otherId = user.Id;
                    else sitterId = user.Id;
                }
                var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
                db.FamilyProfiles.Add(new FamilyProfile { UserId = ownerId, City = city, NumberOfChildren = 2, YoungestChildAgeYears = 3, OldestChildAgeYears = 7, HealthNotes = "No allergies." });
                db.BabysitterProfiles.Add(new BabysitterProfile { Id = profileId, UserId = sitterId, City = city, Bio = "Test sitter", IsAvailable = true });
                await db.SaveChangesAsync();
            }
            await Login(client, ownerEmail);
            await Login(otherClient, otherEmail);
            await Login(sitterClient, sitterEmail);
            var initial = await client.GetStringAsync("/CareRequests/Create");
            Assert.Contains("Who needs care?", initial);
            Assert.Contains("value=\"2\"", initial); // children pre-filled from the family profile
            draftId = Guid.Parse(Hidden(initial, "draftId"));
            var fields = new Dictionary<string, string> { ["draftId"] = draftId.ToString(), ["version"] = "", ["step"] = "1" };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/CareRequests/Save", new FormUrlEncodedContent(fields))).StatusCode);
            var save = await WizardPost(client, fields, "autosave");
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            var json = JsonDocument.Parse(await save.Content.ReadAsStringAsync());
            fields["version"] = json.RootElement.GetProperty("version").GetString()!;
            Assert.Equal(draftId.ToString(), Hidden(await client.GetStringAsync("/CareRequests/Create"), "draftId"));
            var currentVersion = fields["version"];
            fields["version"] = "stale";
            Assert.Equal(HttpStatusCode.Conflict, (await WizardPost(client, fields, "autosave")).StatusCode);
            fields["version"] = currentVersion;
            Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/CareRequests/Create?draftId={draftId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await WizardPost(otherClient, fields, "exit")).StatusCode);
            fields["Form.NumberOfChildren"] = "0"; // step 1 must reject zero children
            Assert.Equal(HttpStatusCode.OK, (await WizardPost(client, fields, "next")).StatusCode);
            fields["Form.NumberOfChildren"] = "2"; fields["Form.YoungestChildAgeYears"] = "7"; fields["Form.OldestChildAgeYears"] = "3";
            var invalidAges = await WizardPost(client, fields, "next");
            Assert.Contains("can&#x27;t be younger", await invalidAges.Content.ReadAsStringAsync());
            fields["Form.YoungestChildAgeYears"] = "3"; fields["Form.OldestChildAgeYears"] = "7";
            await Next(client, fields, 2);
            fields["Form.ServiceType"] = "999";
            Assert.Equal(HttpStatusCode.OK, (await WizardPost(client, fields, "next")).StatusCode);
            fields["Form.ServiceType"] = "Babysitting";
            await Next(client, fields, 3);
            var day = DateOnly.FromDateTime(DashboardViewModel.Local(DateTimeOffset.UtcNow).AddDays(10).DateTime);
            fields["Form.StartDate"] = day.ToString("yyyy-MM-dd");
            fields["Form.EndDate"] = day.AddDays(-1).ToString("yyyy-MM-dd");
            fields["Form.StartTime"] = "18:00"; fields["Form.EndTime"] = "10:00";
            var invalidDate = await WizardPost(client, fields, "next");
            Assert.Contains("Care must end after", await invalidDate.Content.ReadAsStringAsync());
            fields["Form.EndDate"] = day.AddDays(2).ToString("yyyy-MM-dd");
            await Next(client, fields, 4);
            fields["Form.City"] = city;
            Assert.Equal(HttpStatusCode.OK, (await WizardPost(client, fields, "next")).StatusCode);
            fields["Form.StreetAddress"] = "42 Private Street";
            fields["Form.Area"] = "Test area";
            fields["Form.AccessNotes"] = "ENTRY-SECRET-4512";
            await Next(client, fields, 5);
            fields["Form.Instructions"] = "Keep the radio on.\nShort walks only.";
            fields["Form.BudgetMin"] = "120"; fields["Form.BudgetMax"] = "100";
            fields["Form.PreferredSitterId"] = profileId.ToString();
            Assert.Equal(HttpStatusCode.OK, (await WizardPost(client, fields, "next")).StatusCode);
            fields["Form.BudgetMax"] = "150";
            fields["Form.PreferredSitterId"] = Guid.NewGuid().ToString();
            Assert.Equal(HttpStatusCode.OK, (await WizardPost(client, fields, "next")).StatusCode);
            fields["Form.PreferredSitterId"] = profileId.ToString();
            await Next(client, fields, 6);
            Assert.Equal(HttpStatusCode.OK, (await WizardPost(client, fields, "submit")).StatusCode);
            fields["Form.CareConfirmed"] = "true";
            var sent = await WizardPost(client, fields, "submit");
            Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
            Assert.Contains("Submitted", sent.Headers.Location!.ToString());
            var repeated = await WizardPost(client, fields, "submit");
            Assert.Equal(sent.Headers.Location, repeated.Headers.Location);
            var submitted = await client.GetStringAsync(sent.Headers.Location);
            Assert.Contains("Your request is on its way", submitted);
            Assert.Contains("Awaiting a sitter", submitted);
            Assert.Contains("2 children", await client.GetStringAsync($"/CareRequests/Details/{draftId}"));
            Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/CareRequests/Details/{draftId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/CareRequests/Status/{draftId}")).StatusCode);
            var inbox = await sitterClient.GetStringAsync($"/SitterRequests?id={draftId}");
            Assert.Contains("2 children", inbox);
            Assert.DoesNotContain("ENTRY-SECRET", inbox);
            Assert.DoesNotContain("42 Private Street", inbox);
            var accepted = await Post(sitterClient, "/SitterRequests/Decide", new()
            {
                ["RequestId"] = draftId.ToString(), ["Version"] = Hidden(inbox, "Version"), ["Decision"] = "accept"
            });
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
            Assert.Contains("A sitter accepted", await client.GetStringAsync($"/CareRequests/Status/{draftId}"));
            using var verify = factory.Services.CreateScope();
            var database = verify.ServiceProvider.GetRequiredService<BabySittingDbContext>();
            var request = await database.ChildcareRequests.SingleAsync(r => r.Id == draftId);
            Assert.Equal(ownerId, request.CustomerId);
            Assert.Equal(120m, request.Budget);
            Assert.Equal(ScheduleMath.Utc(day, new(18, 0)), request.StartsAtUtc);
            Assert.Equal(ScheduleMath.Utc(day.AddDays(2), new(10, 0)), request.EndsAtUtc);
            Assert.Equal(2, request.NumberOfChildren);
            Assert.Equal(7, request.OldestChildAgeYears);
            Assert.NotNull(request.FamilyProfileId);
            Assert.Contains("ENTRY-SECRET", request.ServiceAddress);
            Assert.DoesNotContain("ENTRY-SECRET", request.Description);
            Assert.Single(await database.BabysittingOffers.Where(o => o.RequestId == draftId).ToListAsync());
            Assert.Single(await database.Notifications.Where(n => n.SourceKey == "owner-request:" + draftId).ToListAsync());
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
            await db.Notifications.Where(n => n.UserId == sitterId || n.UserId == ownerId).ExecuteDeleteAsync();
            await db.ParentRequestDrafts.Where(d => d.CustomerId == ownerId).ExecuteDeleteAsync();
            await db.BabysittingBookings.Where(b => b.CustomerId == ownerId).ExecuteDeleteAsync();
            await db.BabysittingOffers.Where(o => o.Request.CustomerId == ownerId).ExecuteDeleteAsync();
            await db.ChildcareRequests.Where(r => r.CustomerId == ownerId).ExecuteDeleteAsync();
            await db.FamilyProfiles.Where(f => f.UserId == ownerId || f.UserId == otherId).ExecuteDeleteAsync();
            await db.BabysitterProfiles.Where(p => p.UserId == sitterId).ExecuteDeleteAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var email in new[] { ownerEmail, otherEmail, sitterEmail })
            {
                var user = await users.FindByEmailAsync(email);
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
        }
    }
    private static string Hidden(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, $"<input(?=[^>]*name=\"{name}\")(?=[^>]*value=\"([^\"]*)\")[^>]*>").Groups[1].Value);
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync("/Identity/Account/Login");
        fields["__RequestVerificationToken"] = Hidden(html, "__RequestVerificationToken");
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
    private static async Task Login(HttpClient client, string email) => Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "Test!Password123" })).StatusCode);
    private static Task<HttpResponseMessage> WizardPost(HttpClient client, Dictionary<string, string> fields, string command)
    {
        fields["command"] = command;
        return Post(client, "/CareRequests/Save", fields);
    }
    private static async Task Next(HttpClient client, Dictionary<string, string> fields, int next)
    {
        var response = await WizardPost(client, fields, "next");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, body);
        var html = await client.GetStringAsync(response.Headers.Location);
        Assert.Contains($"data-step=\"{next}\"", html);
        fields["step"] = next.ToString(); fields["version"] = Hidden(html, "version");
    }
}
