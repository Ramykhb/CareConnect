using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using BabySitting.Data;
using BabySitting.Services.Auth;

namespace BabySitting.Services.Notifications;

// A coalesced wake-up signal only. Delivery IDs and retry state live in the database,
// so a full channel, a process restart or a failed post-commit wake-up cannot lose mail.
public sealed class NotificationEmailQueue
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public void Enqueue(Guid notificationId) => channel.Writer.TryWrite(true);
    public async Task WaitAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await channel.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}

public sealed class NotificationEmailWorker(NotificationEmailQueue queue, IServiceScopeFactory scopes,
    IOptions<AuthOriginOptions> origin, ILogger<NotificationEmailWorker> logger, TimeProvider clock) : BackgroundService
{
    public static TimeSpan RetryDelay(int attempts) => TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 7))));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessPendingAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Notification email recovery scan failed; it will retry."); }
            try { await queue.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    public async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var now = clock.GetUtcNow();
        var ids = await db.Notifications.AsNoTracking().Where(n => n.EmailPending &&
            (n.EmailNextAttemptAtUtc == null || n.EmailNextAttemptAtUtc <= now) &&
            (n.EmailLeaseUntilUtc == null || n.EmailLeaseUntilUtc <= now))
            .OrderBy(n => n.CreatedAtUtc).ThenBy(n => n.Id).Select(n => n.Id).Take(100).ToListAsync(ct);
        foreach (var id in ids) await DeliverAsync(id, ct);
    }

    public async Task<bool> DeliverAsync(Guid id, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var now = clock.GetUtcNow();
        var lease = Guid.NewGuid();
        // One worker owns this delivery for two minutes (SMTP itself times out after 30 seconds).
        var claimed = await db.Notifications.Where(n => n.Id == id && n.EmailPending &&
            (n.EmailNextAttemptAtUtc == null || n.EmailNextAttemptAtUtc <= now) &&
            (n.EmailLeaseUntilUtc == null || n.EmailLeaseUntilUtc <= now)).ExecuteUpdateAsync(s => s
                .SetProperty(n => n.EmailLeaseId, lease).SetProperty(n => n.EmailLeaseUntilUtc, now.AddMinutes(2))
                .SetProperty(n => n.EmailAttempts, n => n.EmailAttempts + 1), ct);
        if (claimed == 0) return false;
        var owned = db.Notifications.Where(n => n.Id == id && n.EmailLeaseId == lease);
        try
        {
            var sent = await SendAsync(id, ct);
            await owned.ExecuteUpdateAsync(s => s.SetProperty(n => n.EmailPending, false)
                .SetProperty(n => n.EmailSentAtUtc, sent ? clock.GetUtcNow() : (DateTimeOffset?)null)
                .SetProperty(n => n.EmailLeaseId, (Guid?)null).SetProperty(n => n.EmailLeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(n => n.EmailNextAttemptAtUtc, (DateTimeOffset?)null).SetProperty(n => n.EmailLastError, (string?)null), ct);
            return sent;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var attempts = await owned.Select(n => n.EmailAttempts).SingleOrDefaultAsync(ct);
            await owned.ExecuteUpdateAsync(s => s.SetProperty(n => n.EmailNextAttemptAtUtc, clock.GetUtcNow().Add(RetryDelay(attempts)))
                .SetProperty(n => n.EmailLeaseId, (Guid?)null).SetProperty(n => n.EmailLeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(n => n.EmailLastError, ex.GetType().Name), ct);
            logger.LogWarning("Notification email {NotificationId} failed ({ErrorType}); retry scheduled.", id, ex.GetType().Name);
            return false;
        }
    }

    public async Task<bool> SendAsync(Guid id, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var item = await db.Notifications.AsNoTracking().Where(n => n.Id == id)
            .Select(n => new { n.Title, n.Body, n.RelativeUrl, n.Category, n.User.Email, n.User.EmailConfirmed, n.User.FirstName, n.User.IsActive,
                Preferences = db.AlertPreferences.FirstOrDefault(p => p.UserId == n.UserId) })
            .SingleOrDefaultAsync(ct);
        if (item is null || !item.IsActive || item.Preferences is null || !item.Preferences.EmailAllows(item.Category)
            || !item.EmailConfirmed || string.IsNullOrWhiteSpace(item.Email)) return false;
        var link = Uri.TryCreate(origin.Value.PublicOrigin, UriKind.Absolute, out var root) && item.RelativeUrl is { Length: > 0 } url && url.StartsWith('/') && !url.StartsWith("//")
            ? new Uri(root, url).AbsoluteUri : null;
        var html = $"<p>Hi {WebUtility.HtmlEncode(item.FirstName)},</p><p>{WebUtility.HtmlEncode(item.Body)}</p>"
            + (link is null ? "" : $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Open in CareConnect</a></p>")
            + "<p style=\"color:#6b7280;font-size:13px\">You get this email because email alerts are on. Change this in your notification settings.</p>";
        await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendEmailAsync(item.Email!, item.Title, html);
        logger.LogInformation("Notification email {NotificationId} sent.", id);
        return true;
    }
}
