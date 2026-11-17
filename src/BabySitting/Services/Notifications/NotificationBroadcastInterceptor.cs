using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using BabySitting.Models;

namespace BabySitting.Services.Notifications;

// SaveChanges may run inside a larger transaction. Buffer its notifications until that
// transaction commits; implicit SaveChanges transactions are already committed on return.
public sealed class NotificationBroadcastInterceptor(LiveNotifications live) : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private sealed class Pending
    {
        public Dictionary<Guid, Notification> Added { get; } = new();
        public HashSet<Guid> Changed { get; } = new();
        public HashSet<Guid> Deleted { get; } = new();
        public void Merge(Pending other)
        {
            foreach (var id in other.Deleted) Added.Remove(id);
            Deleted.UnionWith(other.Deleted);
            foreach (var entry in other.Added) Added[entry.Key] = entry.Value;
            Changed.UnionWith(other.Changed);
        }
    }
    private readonly ConditionalWeakTable<DbContext, Pending> captured = new();
    private readonly ConditionalWeakTable<DbContext, Dictionary<Guid, Pending>> transactions = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    { Capture(eventData.Context); return result; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Capture(eventData.Context); return ValueTask.FromResult(result); }
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    { SavedAsync(eventData.Context).GetAwaiter().GetResult(); return result; }
    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    { await SavedAsync(eventData.Context); return result; }
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => DiscardCapture(eventData.Context);
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    { DiscardCapture(eventData.Context); return Task.CompletedTask; }

    void IDbTransactionInterceptor.TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        CommittedAsync(eventData.Context, eventData.TransactionId).GetAwaiter().GetResult();
    Task IDbTransactionInterceptor.TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken) =>
        CommittedAsync(eventData.Context, eventData.TransactionId);
    void IDbTransactionInterceptor.TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        DiscardTransaction(eventData.Context, eventData.TransactionId);
    Task IDbTransactionInterceptor.TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken)
    { DiscardTransaction(eventData.Context, eventData.TransactionId); return Task.CompletedTask; }
    void IDbTransactionInterceptor.TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        DiscardTransaction(eventData.Context, eventData.TransactionId);
    Task IDbTransactionInterceptor.TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken)
    { DiscardTransaction(eventData.Context, eventData.TransactionId); return Task.CompletedTask; }

    private void Capture(DbContext? context)
    {
        if (context is null) return;
        var work = new Pending();
        foreach (var entry in context.ChangeTracker.Entries<Notification>())
        {
            if (entry.State == EntityState.Added) work.Added[entry.Entity.Id] = entry.Entity;
            else if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                work.Changed.Add(entry.Entity.UserId);
                if (entry.State == EntityState.Deleted) work.Deleted.Add(entry.Entity.Id);
            }
        }
        captured.AddOrUpdate(context, work);
    }
    private async Task SavedAsync(DbContext? context)
    {
        if (context is null || !captured.TryGetValue(context, out var work)) return;
        captured.Remove(context);
        if (context.Database.CurrentTransaction is { } transaction)
        {
            var buffer = transactions.GetOrCreateValue(context);
            // A disposed, uncommitted transaction must never leak into a later transaction.
            foreach (var id in buffer.Keys.Where(id => id != transaction.TransactionId).ToArray()) buffer.Remove(id);
            if (!buffer.TryGetValue(transaction.TransactionId, out var pending)) buffer[transaction.TransactionId] = pending = new();
            pending.Merge(work);
        }
        else
        {
            transactions.Remove(context);
            await PublishAsync(work);
        }
    }
    private Task CommittedAsync(DbContext? context, Guid transactionId)
    {
        if (context is not null && transactions.TryGetValue(context, out var buffer) && buffer.Remove(transactionId, out var work))
            return PublishAsync(work);
        return Task.CompletedTask;
    }
    private void DiscardCapture(DbContext? context) { if (context is not null) captured.Remove(context); }
    private void DiscardTransaction(DbContext? context, Guid id)
    { if (context is not null && transactions.TryGetValue(context, out var buffer)) buffer.Remove(id); }
    private async Task PublishAsync(Pending work)
    {
        foreach (var n in work.Added.Values) await live.PushAsync(n, silent: n.ReadAtUtc is not null);
        foreach (var userId in work.Changed) await live.ChangedAsync(userId);
    }
}
