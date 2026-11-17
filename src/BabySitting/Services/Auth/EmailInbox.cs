using System.Net;
namespace BabySitting.Services.Auth;

// A local Mailpit inbox must not be advertised when mail is delivered through a real relay.
public static class EmailInbox
{
    public static string? Url(IConfiguration configuration, IWebHostEnvironment environment)
    {
        if (!Uri.TryCreate(configuration["Email:InboxUrl"], UriKind.Absolute, out var inbox)) return null;
        var host = configuration["Email:Host"];
        var localDelivery = !string.Equals(configuration["Email:Provider"], "Brevo", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        if (inbox.IsLoopback && !localDelivery) return null;
        return inbox.Scheme == "https" || environment.IsDevelopment() && inbox.IsLoopback && inbox.Scheme == "http"
            ? inbox.AbsoluteUri : null;
    }
}
