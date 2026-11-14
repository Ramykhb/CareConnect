using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Auth;
using PetSitting.Services.Bookings;
using PetSitting.ViewModels;
using Xunit;

namespace PetSitting.Tests;

public sealed class OwnerBookingTests
{
    [Theory]
    [InlineData("/OwnerBookings")]
    [InlineData("/OwnerBookings/Details/00000000-0000-0000-0000-000000000001")]
    [InlineData("/OwnerBookings/Updates")]
    public async Task Owner_booking_pages_require_sign_in(string path)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var result = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.Contains("/Identity/Account/Login", result.Headers.Location!.ToString());
    }

    [Fact]
    public void Booking_statuses_and_cancellation_use_actual_payment_and_care_state()
    {
        var request = new PetCareRequest { Status = RequestStatus.Booked, StartsAtUtc = DateTimeOffset.UtcNow.AddDays(2) };
        var booking = new PetBooking { Status = BookingStatus.AwaitingPayment, TotalAmount = 120, Currency = "USD" };
        var item = new OwnerBookingItem { Request = request, Booking = booking, Now = DateTimeOffset.UtcNow };
        Assert.Equal("accepted", item.Bucket);
        Assert.Equal("Payment pending", item.Status);
        booking.Payments.Add(new Payment { Amount = 120, Currency = "EUR", Status = PaymentStatus.Succeeded });
        Assert.Equal("Payment pending", item.Status);
        booking.Payments.Add(new Payment { Amount = 120, Currency = "USD", Status = PaymentStatus.Succeeded, Provider = "RealProvider" });
        Assert.Equal("Accepted", item.Status);
        Assert.False(OwnerBookingService.CanCancel(request, booking, item.Now));
        booking.Payments.Last().Provider = "Dummy";
        Assert.True(OwnerBookingService.CanCancel(request, booking, item.Now));
        booking.Status = BookingStatus.InProgress;
        Assert.Equal("progress", item.Bucket);
        Assert.False(OwnerBookingService.CanCancel(request, booking, item.Now));
        booking.Status = BookingStatus.Completed;
        Assert.Equal("completed", item.Bucket);
    }

    [Fact]
    public void List_pagination_clamps_out_of_range_pages()
    {
        var model = new OwnerBookingsViewModel { Page = 100, Items = Enumerable.Range(0, 17).Select(_ => new OwnerBookingItem { Request = new PetCareRequest { StartsAtUtc = DateTimeOffset.UtcNow.AddDays(2) }, Now = DateTimeOffset.UtcNow }).ToArray() };
        Assert.Equal(3, model.CurrentPage);
        Assert.Single(model.Rows);
        Assert.Equal(17, model.Count("pending"));
    }

    [SqlServerFact]
    public async Task Owner_list_details_payment_receipt_review_cancel_and_book_again_work()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var ownerClient = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var otherClient = factory.CreateClient(new() { AllowAutoRedirect = false });
        var key = Guid.NewGuid().ToString("N");
        var ownerEmail = $"ob-owner-{key}@example.test"; var otherEmail = $"ob-other-{key}@example.test"; var sitterEmail = $"ob-sitter-{key}@example.test";
        var owner = Guid.Empty; var other = Guid.Empty; var sitter = Guid.Empty;
        var profile = Guid.NewGuid(); var pet = Guid.NewGuid();
        var pending = Guid.NewGuid(); var accepted = Guid.NewGuid(); var completed = Guid.NewGuid(); var rejected = Guid.NewGuid();
        var acceptedBooking = Guid.NewGuid(); var completedBooking = Guid.NewGuid();
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var registration = scope.ServiceProvider.GetRequiredService<RegistrationService>();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                foreach (var (email, role) in new[] { (ownerEmail, "Customer"), (otherEmail, "Customer"), (sitterEmail, "Sitter") })
                {
                    var (result, user) = await registration.RegisterAsync(email, "Test!Password123", email == sitterEmail ? "Omar" : "Owner", "BookingTest", role);
                    Assert.True(result.Succeeded); user!.EmailConfirmed = true;
                    Assert.True((await users.UpdateAsync(user)).Succeeded);
                    if (email == ownerEmail) owner = user.Id; else if (email == otherEmail) other = user.Id; else sitter = user.Id;
                }
                var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
                db.Pets.Add(new Pet { Id = pet, OwnerId = owner, Name = "Booking Max", Species = "Dog", Breed = "Golden Retriever", AgeYears = 3 });
                db.PetSitterProfiles.Add(new() { Id = profile, UserId = sitter, City = "Beirut", Bio = "Test sitter", HourlyRate = 18, YearsOfExperience = 6, VerificationStatus = VerificationStatus.Verified });
                foreach (var (id, state, offerState) in new[] { (pending, RequestStatus.Open, OfferStatus.Pending), (accepted, RequestStatus.Booked, OfferStatus.Accepted), (completed, RequestStatus.Completed, OfferStatus.Accepted), (rejected, RequestStatus.Open, OfferStatus.Rejected) })
                {
                    var done = id == completed;
                    var start = DateTimeOffset.UtcNow.AddDays(done ? -4 : 4);
                    var request = new PetCareRequest { Id = id, CustomerId = owner, Title = done ? "Completed care for Booking Max" : "Overnight care for Booking Max", City = "Beirut", Description = "Keep the radio on.\nShort walks only.", ServiceAddress = "42 PRIVATE Street · ENTRY-SECRET", ServiceType = ServiceType.PetSitting, Budget = 120, StartsAtUtc = start, EndsAtUtc = start.AddDays(2), Status = state };
                    request.Pets.Add(new() { PetId = pet });
                    var offer = new PetCareOffer { RequestId = id, SitterProfileId = profile, Status = offerState, Amount = 120, DecidedAtUtc = offerState == OfferStatus.Accepted ? start.AddDays(-1) : null };
                    db.PetCareRequests.Add(request); db.PetCareOffers.Add(offer);
                    if (id == accepted || done)
                    {
                        var booking = new PetBooking { Id = done ? completedBooking : acceptedBooking, RequestId = id, OfferId = offer.Id, CustomerId = owner, SitterProfileId = profile, StartsAtUtc = start, EndsAtUtc = start.AddDays(2), TotalAmount = 120, Status = done ? BookingStatus.Completed : BookingStatus.AwaitingPayment,
                            StartedAtUtc = done ? start : null, CompletedAtUtc = done ? start.AddDays(2) : null };
                        db.PetBookings.Add(booking);
                        if (done) db.Payments.Add(new() { BookingId = booking.Id, PayerId = owner, Amount = 120, Currency = "USD", Status = PaymentStatus.Succeeded, Provider = "Dummy", ProviderTransactionId = "DEMO-COMPLETED-" + key, PaidAtUtc = start.AddDays(-1) });
                    }
                }
                await db.SaveChangesAsync();
            }
            await Login(ownerClient, ownerEmail); await Login(otherClient, otherEmail);
            var list = await ownerClient.GetStringAsync("/OwnerBookings");
            Assert.Contains("My requests &amp; bookings", list);
            Assert.Contains("Payment pending", list);
            Assert.Contains("Rejected", list);
            Assert.Contains("Booking Max", list);
            var filtered = await ownerClient.GetStringAsync("/OwnerBookings?status=completed&search=Omar&service=PetSitting&petId=" + pet);
            Assert.Contains("Completed", filtered);
            Assert.DoesNotContain("Payment pending", filtered);
            Assert.Contains("No requests here yet", await ownerClient.GetStringAsync("/OwnerBookings?search=unknown"));
            var pendingPage = await ownerClient.GetStringAsync($"/OwnerBookings/Details/{pending}");
            Assert.Contains("Waiting for Omar", pendingPage);
            Assert.Contains("ENTRY-SECRET", pendingPage);
            Assert.Contains("Confirm cancellation", pendingPage);
            Assert.DoesNotContain("Record demo payment", pendingPage);
            Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/OwnerBookings/Details/{pending}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/OwnerBookings/DetailUpdates/{completed}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/OwnerBookings/Receipt/{completed}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Post(otherClient, "/OwnerBookings/Pay", new() { ["id"] = acceptedBooking.ToString(), ["clientToken"] = Guid.NewGuid().ToString() })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsync("/OwnerBookings/Cancel", new FormUrlEncodedContent(new Dictionary<string,string> { ["id"] = pending.ToString(), ["reason"] = "Cancel" }))).StatusCode);
            var acceptedPage = await ownerClient.GetStringAsync($"/OwnerBookings/Details/{accepted}");
            var paymentForm = new Dictionary<string, string> { ["id"] = acceptedBooking.ToString(), ["version"] = Hidden(acceptedPage, "version"), ["clientToken"] = Hidden(acceptedPage, "clientToken") };
            Assert.NotEmpty(paymentForm["clientToken"]);
            var stale = new Dictionary<string, string>(paymentForm) { ["version"] = "stale" };
            Assert.Equal(HttpStatusCode.Redirect, (await Post(ownerClient, "/OwnerBookings/Pay", stale)).StatusCode);
            using (var check = factory.Services.CreateScope()) Assert.False(await check.ServiceProvider.GetRequiredService<PetSittingDbContext>().Payments.AnyAsync(p => p.BookingId == acceptedBooking));
            Assert.Equal(HttpStatusCode.Redirect, (await Post(ownerClient, "/OwnerBookings/Pay", paymentForm)).StatusCode);
            await Post(ownerClient, "/OwnerBookings/Pay", paymentForm);
            var paidPage = await ownerClient.GetStringAsync($"/OwnerBookings/Details/{acceptedBooking}");
            Assert.Contains("Payment successful", paidPage);
            Assert.DoesNotContain("Record demo payment", paidPage);
            var completedPage = await ownerClient.GetStringAsync($"/OwnerBookings/Details/{completed}");
            Assert.Contains("Leave a review", completedPage);
            Assert.Contains("Book Omar again", completedPage);
            Assert.DoesNotContain("Confirm cancellation", completedPage);
            var receipt = await ownerClient.GetAsync($"/OwnerBookings/Receipt/{completed}");
            receipt.EnsureSuccessStatusCode();
            Assert.Contains("attachment", receipt.Content.Headers.ContentDisposition!.ToString());
            Assert.Contains("Demo payment", await receipt.Content.ReadAsStringAsync());
            var reviewFields = new Dictionary<string, string> { ["id"] = completedBooking.ToString(), ["rating"] = "5", ["comment"] = "Kind and attentive <script>unsafe</script>" };
            await Post(ownerClient, "/OwnerBookings/Review", reviewFields);
            await Post(ownerClient, "/OwnerBookings/Review", reviewFields);
            var reviewed = await ownerClient.GetStringAsync($"/OwnerBookings/Details/{completed}");
            Assert.Contains("Your review", reviewed);
            Assert.Contains("&lt;script&gt;unsafe&lt;/script&gt;", reviewed);
            Assert.DoesNotContain("<script>unsafe</script>", reviewed);
            Assert.DoesNotContain("Publish review", reviewed);
            var again = await Post(ownerClient, "/OwnerBookings/Again", new() { ["id"] = completed.ToString() });
            Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
            Assert.Contains("draftId", again.Headers.Location!.ToString());
            Assert.Contains("Who needs care?", await ownerClient.GetStringAsync(again.Headers.Location));
            var cancelFields = new Dictionary<string, string> { ["id"] = accepted.ToString(), ["version"] = Hidden(paidPage, "version"), ["reason"] = "Plans changed." };
            await Post(ownerClient, "/OwnerBookings/Cancel", cancelFields);
            await Post(ownerClient, "/OwnerBookings/Cancel", cancelFields);
            var cancelled = await ownerClient.GetStringAsync($"/OwnerBookings/Details/{accepted}");
            Assert.Contains("Cancelled by the owner", cancelled);
            Assert.Contains("Refunded", cancelled);
            using var verify = factory.Services.CreateScope();
            var database = verify.ServiceProvider.GetRequiredService<PetSittingDbContext>();
            Assert.Single(await database.Payments.Where(p => p.BookingId == acceptedBooking).ToArrayAsync());
            Assert.Equal(PaymentStatus.Refunded, (await database.Payments.SingleAsync(p => p.BookingId == acceptedBooking)).Status);
            Assert.Single(await database.Reviews.Where(r => r.BookingId == completedBooking).ToArrayAsync());
            var draft = await database.OwnerRequestDrafts.SingleAsync(d => d.CustomerId == owner);
            var copied = JsonSerializer.Deserialize<OwnerRequestForm>(draft.DataJson)!;
            Assert.Null(copied.StartDate); Assert.Null(copied.EndDate); Assert.False(copied.CareConfirmed);
            Assert.Equal(pet, Assert.Single(copied.PetIds)); Assert.Equal(profile, copied.PreferredSitterId);
        }
        finally
        {
            using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
            await db.Notifications.Where(n => n.UserId == owner || n.UserId == sitter).ExecuteDeleteAsync();
            await db.OwnerRequestDrafts.Where(d => d.CustomerId == owner).ExecuteDeleteAsync();
            await db.Reviews.Where(r => r.ReviewerId == owner).ExecuteDeleteAsync();
            await db.Payments.Where(p => p.PayerId == owner).ExecuteDeleteAsync();
            await db.PetBookings.Where(b => b.CustomerId == owner).ExecuteDeleteAsync();
            await db.PetCareOffers.Where(o => o.Request.CustomerId == owner).ExecuteDeleteAsync();
            await db.PetCareRequestPets.Where(p => p.PetCareRequest.CustomerId == owner).ExecuteDeleteAsync();
            await db.PetCareRequests.Where(r => r.CustomerId == owner).ExecuteDeleteAsync();
            await db.Pets.Where(p => p.OwnerId == owner).ExecuteDeleteAsync();
            await db.PetSitterProfiles.Where(p => p.UserId == sitter).ExecuteDeleteAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var email in new[] { ownerEmail, otherEmail, sitterEmail }) { var user = await users.FindByEmailAsync(email); if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded); }
        }
    }
    private static string Hidden(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, $"<input(?=[^>]*name=\"{name}\")(?=[^>]*value=\"([^\"]*)\")[^>]*>").Groups[1].Value);
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = Hidden(await client.GetStringAsync("/Identity/Account/Login"), "__RequestVerificationToken");
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
    private static async Task Login(HttpClient client, string email) => Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "Test!Password123" })).StatusCode);
}
