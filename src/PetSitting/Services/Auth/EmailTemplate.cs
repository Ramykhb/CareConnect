using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace PetSitting.Services.Auth;

public sealed record EmailContent(string Html, string PlainText);

// Applied at the SMTP boundary so built-in Identity email changes and resends get the
// same design as our registration and recovery emails. Action URLs are never rebuilt.
public static class EmailTemplate
{
    private static readonly Regex ActionLink = new("<a\\b[^>]*\\bhref\\s*=\\s*(?:\"(?<url>[^\"]+)\"|'(?<url>[^']+)')",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static EmailContent Create(string subject, string htmlMessage, string publicOrigin, TimeSpan tokenLifetime)
    {
        var origin = new Uri(publicOrigin, UriKind.Absolute);
        var candidate = WebUtility.HtmlDecode(ActionLink.Match(htmlMessage).Groups["url"].Value);
        var knownAction = Uri.TryCreate(candidate, UriKind.Absolute, out var action)
            && action.Scheme is "http" or "https"
            && string.Equals(action.GetLeftPart(UriPartial.Authority), origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
        var path = knownAction ? action!.AbsolutePath.TrimEnd('/') : "";
        var reset = path.Equals("/Identity/Account/ResetPassword", StringComparison.OrdinalIgnoreCase);
        var change = path.Equals("/Identity/Account/ConfirmEmailChange", StringComparison.OrdinalIgnoreCase);
        var confirm = path.Equals("/Identity/Account/ConfirmEmail", StringComparison.OrdinalIgnoreCase);
        var transactional = reset || change || confirm;
        var heading = reset ? "A fresh start for your password." : change ? "A new email. The same great care." : confirm ? "One little step. A lot more peace of mind." : subject;
        var eyebrow = reset ? "LET’S GET YOU BACK IN" : change ? "YOUR ACCOUNT, UPDATED" : "THOUGHTFUL CARE STARTS HERE";
        var intro = reset ? "We received a request to reset your CareConnect password. Choose a new one using your secure link below."
            : change ? "Confirm this email address to finish updating your CareConnect account and keep your care plans connected."
            : confirm ? "Welcome to CareConnect. Verify your email address to finish setting up your account. Your next chapter of care is just a click away."
            : "An update from your CareConnect team.";
        var label = reset ? "Reset my password" : change ? "Confirm new email" : "Verify my email";
        var safety = reset ? "Didn’t request a password reset? You can safely ignore this email. Your password will stay the same."
            : change ? "Didn’t request this change? Don’t confirm it. Sign in to CareConnect to review your account security."
            : "If you didn’t create a CareConnect account, you can safely ignore this email.";
        var lifetime = tokenLifetime.TotalHours >= 1
            ? tokenLifetime.TotalHours.ToString("0.##", CultureInfo.InvariantCulture) + (tokenLifetime.TotalHours == 1 ? " hour" : " hours")
            : tokenLifetime.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture) + (tokenLifetime.TotalMinutes == 1 ? " minute" : " minutes");
        var expiry = "For your security, this link expires in " + lifetime + ".";
        var href = Encode(candidate);
        var site = Encode(origin.AbsoluteUri.TrimEnd('/') + "/");
        var content = transactional ? $$"""
            <p style="margin:0 0 26px;color:#626f61;font-size:16px;line-height:1.8;">{{Encode(intro)}}</p>
            <table role="presentation" cellspacing="0" cellpadding="0" border="0" style="margin:0 0 26px;"><tr><td bgcolor="#191b1c" style="border-radius:28px;text-align:center;mso-padding-alt:16px 28px;">
            <a href="{{href}}" style="display:inline-block;border:1px solid #191b1c;border-radius:28px;padding:16px 28px;font-family:Arial,Helvetica,sans-serif;font-size:15px;font-weight:bold;line-height:20px;color:#ecff8c;text-decoration:none;">{{Encode(label)}} &nbsp;&#8594;</a>
            </td></tr></table>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0"><tr><td bgcolor="#eff4e8" style="padding:16px 18px;border:1px solid #e0e8d5;border-radius:14px;color:#526346;font-size:13px;line-height:1.7;">{{Encode(expiry)}}<br />Use this link only for your own account.</td></tr></table>
            <p style="margin:24px 0 0;color:#74806e;font-size:13px;line-height:1.8;">{{Encode(safety)}}</p>
            """ : "<div style=\"font-size:15px;line-height:1.8;color:#526346;\">" + htmlMessage + "</div>";
        var fallback = transactional ? $$"""
            <tr><td class="email-padding" style="padding:24px 36px;border-top:1px solid #e7ece1;background:#fafbf7;">
            <p style="margin:0 0 9px;color:#6f7b67;font-size:11px;line-height:1.7;">Button not working? Copy and paste this link into your browser:</p>
            <p style="margin:0;word-break:break-all;overflow-wrap:anywhere;font-size:11px;line-height:1.8;"><a href="{{href}}" style="color:#536b3a;text-decoration:underline;word-break:break-all;overflow-wrap:anywhere;">{{href}}</a></p>
            </td></tr>
            """ : "";
        var preview = transactional ? intro : subject;
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width,initial-scale=1" /><meta name="color-scheme" content="light" /><title>{{Encode(subject)}}</title>
            <style>@media only screen and (max-width:620px){.email-outer{padding:16px 10px!important}.email-padding{padding-left:24px!important;padding-right:24px!important}.email-heading{font-size:29px!important;line-height:1.2!important}.email-portal{display:block!important;padding-top:8px!important;padding-left:0!important}.email-container{width:100%!important} }</style></head>
            <body style="margin:0;padding:0;width:100%;background:#f5f7f3;color:#191b1c;font-family:Arial,Helvetica,sans-serif;-webkit-text-size-adjust:100%;">
            <div style="display:none;max-height:0;max-width:0;overflow:hidden;opacity:0;color:transparent;mso-hide:all;">{{Encode(preview)}}</div>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" bgcolor="#f5f7f3"><tr><td class="email-outer" align="center" style="padding:40px 16px;">
            <!--[if mso]><table role="presentation" width="600" align="center"><tr><td><![endif]-->
            <table class="email-container" role="presentation" width="600" cellspacing="0" cellpadding="0" border="0" bgcolor="#ffffff" style="width:100%;max-width:600px;border:1px solid #dfe6d9;border-radius:24px;border-collapse:separate;overflow:hidden;">
            <tr><td class="email-padding" bgcolor="#191b1c" style="padding:27px 36px;border-radius:23px 23px 0 0;">
            <span style="display:inline-block;color:#ffffff;font-size:24px;font-weight:bold;letter-spacing:-1px;">CareConnect<span style="color:#ecff8c;">.</span></span>
            <span class="email-portal" style="display:inline-block;color:#d2dec2;font-size:11px;letter-spacing:.5px;padding-left:20px;">PET CARE PORTAL</span>
            </td></tr>
            <tr><td class="email-padding" style="padding:36px 36px 32px;">
            <p style="margin:0 0 14px;font-size:10px;font-weight:bold;letter-spacing:1.6px;color:#657a4d;">{{Encode(eyebrow)}}</p>
            <h1 class="email-heading" style="margin:0 0 22px;font-size:36px;font-weight:normal;line-height:1.15;letter-spacing:-1.2px;color:#191b1c;">{{Encode(heading)}}</h1>
            {{content}}
            </td></tr>
            {{fallback}}
            <tr><td class="email-padding" style="padding:22px 36px;border-top:1px solid #e7ece1;">
            <p style="margin:0;color:#64765b;font-size:12px;line-height:1.7;">Happy pets. A little more peace of mind.</p>
            <p style="margin:8px 0 0;font-size:11px;line-height:1.7;color:#7c8675;"><a href="{{site}}" style="color:#52683e;text-decoration:none;">CareConnect</a> &nbsp;&middot;&nbsp; Thoughtful care, every step.</p>
            </td></tr></table>
            <!--[if mso]></td></tr></table><![endif]-->
            <p style="margin:20px 0 0;font-size:10px;line-height:1.8;color:#7b8673;">This is an account email from CareConnect. Please keep your secure links private.</p>
            </td></tr></table></body></html>
            """;
        var plain = transactional
            ? $"CareConnect - PET CARE PORTAL\n\n{heading}\n\n{intro}\n\n{label}:\n{candidate}\n\n{expiry}\n\n{safety}\n\nHappy pets. A little more peace of mind.\n{origin.AbsoluteUri}"
            : "CareConnect - PET CARE PORTAL\n\n" + subject + "\n\n" + PlainText(htmlMessage) + "\n\n" + origin.AbsoluteUri;
        return new(html, plain);
    }

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);
    private static string PlainText(string html) => WebUtility.HtmlDecode(Regex.Replace(
        Regex.Replace(ActionLink.Replace(html, m => m.Groups["url"].Value + " "), @"</p>|<br\s*/?>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)),
        "<[^>]+>", "", RegexOptions.None, TimeSpan.FromSeconds(1))).Trim();
}
