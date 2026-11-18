using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Localization;
using BabySitting.Services.Notifications;
using Xunit;

namespace BabySitting.Tests;

// Email alerts (#12a) and the saved language (#12b).
public sealed class AlertsAndLanguageTests
{
    private sealed class Mailbox : IEmailSender
    {
        public ConcurrentQueue<(string To, string Subject, string Html)> Sent { get; } = new();
        public Task SendEmailAsync(string email, string subject, string htmlMessage) { Sent.Enqueue((email, subject, htmlMessage)); return Task.CompletedTask; }
    }

    [Fact]
    public void Email_alerts_are_opt_in_per_category()
    {
        var preferences = new AlertPreferences();
        Assert.All(Enum.GetValues<NotificationCategory>(), c => Assert.False(preferences.EmailAllows(c)));
        preferences.EmailBookings = true;
        Assert.True(preferences.EmailAllows(NotificationCategory.Booking));
        Assert.False(preferences.EmailAllows(NotificationCategory.Payment));
        Assert.False(preferences.EmailAllows(NotificationCategory.General));
    }

    [Fact]
    public async Task Portal_text_follows_the_display_language_and_falls_back_to_English()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        var text = factory.Services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar");
            Assert.Equal("الإعدادات", text["Settings"].Value);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
            Assert.Equal("Paramètres", text["Settings"].Value);
            Assert.Equal("Something not translated", text["Something not translated"].Value);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            Assert.Equal("Settings", text["Settings"].Value);
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    [SqlServerFact]
    public async Task Notifications_are_emailed_only_when_switched_on_and_the_language_rides_in_the_sign_in()
    {
        var mail = new Mailbox();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development")
            .ConfigureServices(s => { s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(mail); }));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var user = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Alert", LastName = "Test", UserName = "alerts-" + Guid.NewGuid().ToString("N"),
            Email = $"alerts-{Guid.NewGuid():N}@example.test", EmailConfirmed = true, PreferredLanguage = "fr", SecurityStamp = Guid.NewGuid().ToString() };
        // Already-read records are never queued, so only the explicit SendAsync calls below can email.
        Notification Note(NotificationCategory category) => new() { User = user, Category = category, Title = category + " update", Body = "Body <b>text</b>", RelativeUrl = "/Notifications", ReadAtUtc = DateTimeOffset.UtcNow };
        var booking = Note(NotificationCategory.Booking); var payment = Note(NotificationCategory.Payment);
        db.AddRange(user, booking, payment);
        await db.SaveChangesAsync();
        try
        {
            var worker = factory.Services.GetRequiredService<NotificationEmailWorker>();
            Assert.False(await worker.SendAsync(booking.Id));                      // no preferences saved: email stays off
            db.AlertPreferences.Add(new AlertPreferences { UserId = user.Id, EmailBookings = true });
            await db.SaveChangesAsync();
            Assert.True(await worker.SendAsync(booking.Id));
            Assert.False(await worker.SendAsync(payment.Id));                      // payments not switched on
            var sent = Assert.Single(mail.Sent);
            Assert.Equal(user.Email, sent.To);
            Assert.Equal("Booking update", sent.Subject);
            Assert.Contains("Body &lt;b&gt;text&lt;/b&gt;", sent.Html);             // body is encoded
            Assert.Contains("/Notifications\"", sent.Html);

            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, false));
            Assert.False(await worker.SendAsync(booking.Id));                      // unconfirmed address

            var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(user);
            Assert.Equal("fr", principal.FindFirst(UserLanguage.ClaimType)?.Value);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Notifications.Where(n => n.UserId == user.Id).ExecuteDeleteAsync();
            await db.AlertPreferences.Where(p => p.UserId == user.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
        }
    }
}
