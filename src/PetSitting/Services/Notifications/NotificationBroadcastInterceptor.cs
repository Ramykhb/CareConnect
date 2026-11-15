using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PetSitting.Models;

namespace PetSitting.Services.Notifications;

// Every service already saves notifications through EF Core. Watching SaveChanges pushes each new
// notification live, and tells the owner's tabs to refresh when one is changed, without touching those services.
public sealed class NotificationBroadcastInterceptor(LiveNotifications live) : SaveChangesInterceptor
{
    private sealed record Pending(List<Notification> Added, HashSet<Guid> Changed);
    private readonly ConditionalWeakTable<DbContext, Pending> pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context); return base.SavingChanges(eventData, result);
    }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context); return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Publish(eventData.Context).GetAwaiter().GetResult(); return base.SavedChanges(eventData, result);
    }
    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await Publish(eventData.Context); return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }
    public override void SaveChangesFailed(DbContextErrorEventData eventData) { if (eventData.Context is { } c) pending.Remove(c); base.SaveChangesFailed(eventData); }

    private void Capture(DbContext? context)
    {
        if (context is null) return;
        var entries = context.ChangeTracker.Entries<Notification>().ToList();
        var added = entries.Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        var changed = entries.Where(e => e.State is EntityState.Modified or EntityState.Deleted).Select(e => e.Entity.UserId).ToHashSet();
        pending.AddOrUpdate(context, new Pending(added, changed));
    }

    private async Task Publish(DbContext? context)
    {
        if (context is null || !pending.TryGetValue(context, out var work)) return;
        pending.Remove(context);
        foreach (var n in work.Added) await live.PushAsync(n, silent: n.ReadAtUtc is not null);
        foreach (var userId in work.Changed) await live.ChangedAsync(userId);
    }
}
