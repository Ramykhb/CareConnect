using System.Net;
using System.Text.Json;
using CareConnect.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CareConnect.Ai.Tests;

public sealed class CareSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeSpan.FromHours(2));
    private static CareSummaryInput Input => new("pet sitting", "DogWalking", "InProgress", new(2026, 11, 21), Now,
        [new("s1", "task", "Walk", Now.AddHours(-3), Now.AddHours(-1), Now.AddHours(-1), false),
         new("s2", "task", "Refresh water", Now.AddHours(-3), null, null, false),
         new("s3", "update", "The sitter reported a concern.", Now, null, null, true, "/OwnerBookings/Details/private-booking#private-update")]);
    private sealed class Fake : ICareSummaryClient
    {
        public bool Available { get; set; } = true;
        public int Calls;
        public bool Fail;
        public async Task<IReadOnlyList<CareSummarySentence>> SummarizeAsync(CareSummaryInput input, CancellationToken ct)
        {
            Calls++; await Task.Delay(10, ct);
            if (Fail) throw new AssistantException("Provider unavailable.");
            return [new("A walk was marked complete.", ["s1"])];
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request);
    }
    [Fact]
    public async Task Facts_and_issues_remain_visible_even_when_AI_omits_them()
    {
        using var cache = new CareSummaryCache(); var provider = new Fake(); var engine = new CareSummaryEngine(provider, cache);
        var result = await engine.GenerateAsync(Guid.NewGuid(), Guid.NewGuid(), Input, default);
        Assert.Equal("ready", result.Status); Assert.Equal(1, result.CompletedTasks); Assert.Equal(1, result.PendingTasks); Assert.Equal(1, result.Issues);
        Assert.Contains(result.Sources, s => s.IsIssue && s.Key == "s3");
    }
    [Fact]
    public async Task Cache_deduplicates_concurrent_calls_and_invalidates_edits_and_owner_changes()
    {
        using var cache = new CareSummaryCache(); var provider = new Fake(); var engine = new CareSummaryEngine(provider, cache);
        var user = Guid.NewGuid(); var booking = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => engine.GenerateAsync(user, booking, Input, default)));
        Assert.Equal(1, provider.Calls);
        await engine.GenerateAsync(user, booking, Input with { AsOf = Now.AddMinutes(1) }, default); Assert.Equal(1, provider.Calls);
        await engine.GenerateAsync(user, booking, Input with { Sources = [.. Input.Sources, new("s4", "update", "New note", Now, null, null, false)] }, default); Assert.Equal(2, provider.Calls);
        await engine.GenerateAsync(Guid.NewGuid(), booking, Input, default); Assert.Equal(3, provider.Calls);
    }
    [Fact]
    public async Task Empty_and_oversized_periods_never_call_Groq()
    {
        using var cache = new CareSummaryCache(); var provider = new Fake(); var engine = new CareSummaryEngine(provider, cache);
        Assert.Equal("empty", (await engine.GenerateAsync(Guid.NewGuid(), Guid.NewGuid(), Input with { Sources = [] }, default)).Status);
        var large = Input with { Sources = Enumerable.Range(0, 81).Select(i => new CareSummarySource("s" + i, "task", "Task", Now, null, null, false)).ToArray() };
        var result = await engine.GenerateAsync(Guid.NewGuid(), Guid.NewGuid(), large, default);
        Assert.Equal("too-many", result.Status); Assert.True(result.Limited); Assert.Equal(0, provider.Calls);
    }
    [Fact]
    public async Task Missing_key_and_provider_failure_return_records_and_can_be_retried()
    {
        using var cache = new CareSummaryCache(); var provider = new Fake { Available = false }; var engine = new CareSummaryEngine(provider, cache);
        var user = Guid.NewGuid(); var booking = Guid.NewGuid();
        var result = await engine.GenerateAsync(user, booking, Input, default);
        Assert.Equal("unavailable", result.Status); Assert.Equal(3, result.Sources.Count); Assert.Equal(0, provider.Calls);
        provider.Available = true; provider.Fail = true;
        Assert.Equal("unavailable", (await engine.GenerateAsync(user, booking, Input, default)).Status);
        provider.Fail = false;
        Assert.Equal("ready", (await engine.GenerateAsync(user, booking, Input, default)).Status); Assert.Equal(2, provider.Calls);
    }
    [Theory]
    [InlineData("{\"sentences\":[]}")]
    [InlineData("{\"sentences\":[{\"text\":\"Invented\",\"sourceKeys\":[\"unknown\"]}]}")]
    [InlineData("{\"sentences\":[{\"text\":\"No evidence\",\"sourceKeys\":[]}]}")]
    public void Unsupported_summaries_are_rejected(string answer) => Assert.Throws<JsonException>(() => GroqCareSummaryClient.Parse(answer, Input.Sources));

    [Fact]
    public async Task Groq_protocol_uses_anonymous_citations_and_excludes_record_urls()
    {
        var client = new GroqCareSummaryClient(new HttpClient(new Handler(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("private-booking", body); Assert.DoesNotContain("private-update", body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("openai/gpt-oss-20b", json.RootElement.GetProperty("model").GetString());
            Assert.True(json.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
            var answer = AssistantJson.Serialize(new { sentences = new[] { new CareSummarySentence("A walk was marked complete.", ["s1"]) } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = answer } } } })) };
        })), Options.Create(new GroqAssistantOptions { Enabled = true, ApiKey = "test-key" }), NullLogger<GroqCareSummaryClient>.Instance);
        var summary = await client.SummarizeAsync(Input, default); Assert.Single(summary); Assert.Equal("s1", summary[0].SourceKeys[0]);
    }
    [Fact]
    public async Task Http_errors_use_safe_fallback_message()
    {
        var client = new GroqCareSummaryClient(new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)))),
            Options.Create(new GroqAssistantOptions { Enabled = true, ApiKey = "test-key" }), NullLogger<GroqCareSummaryClient>.Instance);
        var e = await Assert.ThrowsAsync<AssistantException>(() => client.SummarizeAsync(Input, default)); Assert.DoesNotContain("test-key", e.Message);
    }
}
