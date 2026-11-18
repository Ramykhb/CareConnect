using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using BabySitting.Data;
using BabySitting.Services.Auth;

namespace BabySitting.Services.Notifications;

// Saved notifications are queued here after their transaction commits, so a slow mail server never slows a page down.
public sealed class NotificationEmailQueue
{
    private readonly Channel<Guid> channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
    public void Enqueue(Guid notificationId) => channel.Writer.TryWrite(notificationId);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => channel.Reader.ReadAllAsync(ct);
}

// Emails a notification when the user switched email on for its category and has a confirmed address.
public sealed class NotificationEmailWorker(NotificationEmailQueue queue, IServiceScopeFactory scopes,
    IOptions<AuthOriginOptions> origin, ILogger<NotificationEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            try { await SendAsync(id, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Notification email {NotificationId} could not be sent.", id); }
        }
    }

    public async Task<bool> SendAsync(Guid id, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var item = await db.Notifications.AsNoTracking().Where(n => n.Id == id)
            .Select(n => new { n.Title, n.Body, n.RelativeUrl, n.Category, n.User.Email, n.User.EmailConfirmed, n.User.FirstName,
                Preferences = db.AlertPreferences.FirstOrDefault(p => p.UserId == n.UserId) })
            .SingleOrDefaultAsync(ct);
        if (item is null || item.Preferences is null || !item.Preferences.EmailAllows(item.Category)
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
