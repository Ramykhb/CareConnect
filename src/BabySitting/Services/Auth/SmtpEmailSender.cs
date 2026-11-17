using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Options;

namespace BabySitting.Services.Auth;

public sealed class EmailOptions
{
    public string Provider { get; set; } = "Smtp";
    public string FromName { get; set; } = "CareConnect";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string From { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public bool EnableSsl { get; set; } = true;
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options, IOptions<AuthOriginOptions> origin,
    IOptions<DataProtectionTokenProviderOptions> tokens) : IEmailSender
{
    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        var settings = options.Value;
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = string.IsNullOrWhiteSpace(settings.UserName) ? null
                : new NetworkCredential(settings.UserName, settings.Password)
        };
        var content = EmailTemplate.Create(subject, htmlMessage, origin.Value.PublicOrigin, tokens.Value.TokenLifespan);
        using var message = new MailMessage { From = new MailAddress(settings.From, settings.FromName), Subject = subject,
            SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8, Body = content.PlainText, IsBodyHtml = false };
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(content.Html, Encoding.UTF8, MediaTypeNames.Text.Html));
        message.To.Add(new MailAddress(email));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await client.SendMailAsync(message, timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { throw new SmtpException("Email delivery timed out. Please try again shortly."); }
    }
}
