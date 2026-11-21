using System.Net.Mail;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Notifications;
using Xunit;

namespace PetSitting.Tests;

public sealed class NotificationEmailRetryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Sender : IEmailSender
    {
        public bool Fail = true;
        public int Sent;
        public Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            if (Fail) throw new SmtpException("Unavailable");
            Sent++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Retry_delay_backs_off_and_stays_bounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), NotificationEmailWorker.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(60), NotificationEmailWorker.RetryDelay(2));
        Assert.Equal(TimeSpan.FromHours(1), NotificationEmailWorker.RetryDelay(int.MaxValue));
    }

    [SqlServerFact]
    public async Task Failed_mail_survives_a_worker_restart_and_delivers_only_once_after_recovery()
    {
        var clock = new Clock(); var sender = new Sender();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development")
            .ConfigureServices(s => {
                s.RemoveAll<IHostedService>();
                s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(sender);
                s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(clock);
            }));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), FirstName = "Retry", LastName = "Test",
            Email = "retry@example.test", EmailConfirmed = true };
        var note = new Notification { User = user, Title = "Care update", Body = "Test", Category = NotificationCategory.Booking };
        try
        {
            db.AddRange(user, note, new AlertPreferences { User = user, EmailBookings = true });
            await db.SaveChangesAsync();
            var worker = factory.Services.GetRequiredService<NotificationEmailWorker>();
            Assert.False(await worker.DeliverAsync(note.Id));
            db.ChangeTracker.Clear();
            var failed = await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == note.Id);
            Assert.True(failed.EmailPending);
            Assert.Equal(1, failed.EmailAttempts);
            Assert.Equal(clock.Now.AddSeconds(30), failed.EmailNextAttemptAtUtc);
            Assert.Null(failed.EmailLeaseId);
            Assert.Equal("SmtpException", failed.EmailLastError);
            Assert.False(await worker.DeliverAsync(note.Id)); // backoff hasn't elapsed
            sender.Fail = false;
            clock.Now = clock.Now.AddSeconds(30);
            // A fresh worker has no in-memory delivery IDs; the persisted notification is sufficient.
            var restarted = ActivatorUtilities.CreateInstance<NotificationEmailWorker>(factory.Services, new NotificationEmailQueue());
            Assert.True(await restarted.DeliverAsync(note.Id));
            Assert.False(await restarted.DeliverAsync(note.Id));
            Assert.Equal(1, sender.Sent);
            var delivered = await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == note.Id);
            Assert.False(delivered.EmailPending);
            Assert.Equal(clock.Now, delivered.EmailSentAtUtc);
            Assert.Equal(2, delivered.EmailAttempts);
            Assert.Null(delivered.EmailLastError);
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
