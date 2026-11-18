using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Notifications;
using Xunit;

namespace PetSitting.Tests;

// #15: read notifications older than 90 days are deleted; unread and recent ones stay.
public sealed class NotificationCleanupTests
{
    [SqlServerFact]
    public async Task Only_old_read_notifications_are_deleted()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        var now = DateTimeOffset.UtcNow;
        var user = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Cleanup", LastName = "Test", UserName = "cleanup-" + Guid.NewGuid().ToString("N") };
        Notification Note(int daysAgo, bool read) => new() { User = user, Title = "Old", Body = "Body", CreatedAtUtc = now.AddDays(-daysAgo), ReadAtUtc = read ? now.AddDays(-daysAgo) : null };
        var oldRead = Note(100, true); var oldUnread = Note(100, false); var recentRead = Note(10, true);
        db.AddRange(user, oldRead, oldUnread, recentRead);
        await db.SaveChangesAsync();
        try
        {
            await NotificationCleanupService.DeleteExpiredAsync(db, now);
            var left = await db.Notifications.Where(n => n.UserId == user.Id).Select(n => n.Id).ToListAsync();
            Assert.DoesNotContain(oldRead.Id, left);
            Assert.Contains(oldUnread.Id, left);
            Assert.Contains(recentRead.Id, left);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Notifications.Where(n => n.UserId == user.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
        }
    }
}
