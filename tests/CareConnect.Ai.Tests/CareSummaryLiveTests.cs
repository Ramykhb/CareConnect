using CareConnect.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CareConnect.Ai.Tests;

public sealed class CareSummaryLiveTests
{
    [GroqLiveFact]
    public async Task Groq_summarizes_pet_and_childcare_records_with_citations_and_reported_issues()
    {
        using var http = new HttpClient();
        var client = new GroqCareSummaryClient(http, Options.Create(new GroqAssistantOptions
        { Enabled = true, ApiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY"), AssistantTimeoutSeconds = 45 }), NullLogger<GroqCareSummaryClient>.Instance);
        var now = new DateTimeOffset(2026, 11, 21, 15, 0, 0, TimeSpan.FromHours(2));
        foreach (var app in new[] { "pet sitting", "childcare" })
        {
            CareSummarySource[] records =
            [new("s1", "task", app == "pet sitting" ? "Morning walk" : "Story time", now.AddHours(-3), now.AddHours(-2), now.AddHours(-2), false),
             new("s2", "task", "Evening routine", now.AddHours(-3), now.AddHours(2), null, false),
             new("s3", "update", "The sitter reported that the afternoon meal was declined and informed the customer.", now.AddMinutes(-20), null, null, true)];
            var result = await client.SummarizeAsync(new(app, app == "pet sitting" ? "PetSitting" : "Babysitting", "InProgress", new(2026, 11, 21), now, records), default);
            Assert.InRange(result.Count, 1, 5);
            Assert.All(result, s => Assert.NotEmpty(s.SourceKeys));
            Assert.Contains(result, s => s.SourceKeys.Contains("s3"));
            Assert.DoesNotContain(result, s => s.Text.Contains("no issues", StringComparison.OrdinalIgnoreCase));
        }
    }
}
