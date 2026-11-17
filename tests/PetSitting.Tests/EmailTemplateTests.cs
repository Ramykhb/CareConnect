using System.Net;
using PetSitting.Services.Auth;
using Xunit;

namespace PetSitting.Tests;

public sealed class EmailTemplateTests
{
    [Theory]
    [InlineData("ConfirmEmail", "Verify my email")]
    [InlineData("ConfirmEmailChange", "Confirm new email")]
    [InlineData("ResetPassword", "Reset my password")]
    public void Account_emails_preserve_secure_links_and_include_expiry_and_plain_text(string page, string action)
    {
        const string origin = "https://careconnect.example";
        var url = origin + "/Identity/Account/" + page + "?code=Token%2BWith%2FEscapes&userId=test&returnUrl=%2FDashboard";
        var content = EmailTemplate.Create("Account <update>", "Use <a href='" + WebUtility.HtmlEncode(url) + "'>this secure link</a>.", origin, TimeSpan.FromHours(2));
        var decoded = WebUtility.HtmlDecode(content.Html);
        Assert.Contains("<!doctype html>", content.Html);
        Assert.Contains(action, content.Html);
        Assert.Contains("2 hours", content.Html);
        Assert.Contains("href=\"" + url + "\"", decoded);
        Assert.Contains(url, content.PlainText);
        Assert.DoesNotContain("<a", content.PlainText);
        Assert.DoesNotContain("<update>", content.Html);
        Assert.DoesNotContain("<script", content.Html);
    }

    [Fact]
    public void Other_messages_keep_their_content_inside_the_shared_branding()
    {
        var content = EmailTemplate.Create("Account <update>", "<p>Your account settings were updated. <a href='https://careconnect.example/settings'>Review settings</a>.</p>", "https://careconnect.example", TimeSpan.FromHours(2));
        Assert.Contains("Your account settings were updated.", content.Html);
        Assert.Contains("CareConnect", content.Html);
        Assert.Contains("Account &lt;update&gt;", content.Html);
        Assert.Contains("Your account settings were updated.", content.PlainText);
        Assert.DoesNotContain("<p>", content.PlainText);
        Assert.Contains("https://careconnect.example/settings", content.PlainText);
    }
}
