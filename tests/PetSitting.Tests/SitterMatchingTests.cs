using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PetSitting.Services.Matching;
using Xunit;

namespace PetSitting.Tests;

// #1 sitter matching: rule ranking, the optional Claude re-ranking, and its safe fallbacks.
public sealed class SitterMatchingTests
{
    private static readonly MatchRequest Request = new("PetSitting", 4, 60m, null, null);
    private static readonly MatchCandidate Newcomer = new(Guid.NewGuid(), 1, 10, false, null, 0, 0);
    private static readonly MatchCandidate Trusted = new(Guid.NewGuid(), 6, 12, true, 4.9, 12, 15, true);
    private static readonly MatchCandidate Pricey = new(Guid.NewGuid(), 10, 40, true, 4.0, 3, 4);

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Last; public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Last = request; Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct); return await answer(request).WaitAsync(ct); }   // a real server connection is cancelled too
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }

    private static (SitterMatchingService Service, FakeHandler Handler) Create(bool enabled, Func<HttpRequestMessage, Task<HttpResponseMessage>> answer, string? key = "test-key", int timeout = 8)
    {
        var handler = new FakeHandler(answer);
        var options = Options.Create(new AiMatchingOptions { Enabled = enabled, ApiKey = key, TimeoutSeconds = timeout });
        return (new SitterMatchingService(new Factory(handler), options, NullLogger<SitterMatchingService>.Instance), handler);
    }
    private static Task<HttpResponseMessage> Claude(string text) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}]}", Encoding.UTF8, "application/json")
    });

    [Fact]
    public async Task Rules_put_trusted_affordable_sitters_first_and_explain_why()
    {
        var (service, handler) = Create(false, _ => throw new InvalidOperationException("must not call the AI"));
        var ranked = await service.RankAsync(Request, [Newcomer, Pricey, Trusted]);
        Assert.Equal([Trusted.Id, Pricey.Id, Newcomer.Id], ranked.Select(r => r.Id));
        Assert.All(ranked, r => Assert.False(r.FromAi));
        Assert.Contains("Verified", ranked[0].Reason);
        Assert.Contains("fits your budget", ranked[0].Reason);
        Assert.Equal("Available for your dates", SitterMatchingService.RuleReason(Request with { Budget = null }, Newcomer with { Experience = 0 }));
        Assert.Null(handler.Last);
    }

    [Fact]
    public void Going_over_budget_lowers_the_score()
    {
        Assert.True(SitterMatchingService.Score(Request, Pricey with { HourlyRate = 10 }) > SitterMatchingService.Score(Request, Pricey));
    }

    [Fact]
    public async Task Claude_reorders_the_sitters_and_writes_the_reasons()
    {
        var (service, handler) = Create(true, _ => Claude("Here you go: [{\"id\":\"s3\",\"reason\":\"Cheapest and keen\"},{\"id\":\"s1\",\"reason\":\"Most trusted\"},{\"id\":\"s9\",\"reason\":\"unknown\"},{\"id\":\"s1\",\"reason\":\"dup\"}]"));
        var ranked = await service.RankAsync(Request, [Newcomer, Pricey, Trusted]);
        // Rule order was Trusted (s1), Pricey (s2), Newcomer (s3); the AI picked s3 then s1, and s2 keeps its place after them.
        Assert.Equal([Newcomer.Id, Trusted.Id, Pricey.Id], ranked.Select(r => r.Id));
        Assert.Equal("Cheapest and keen", ranked[0].Reason);
        Assert.True(ranked[0].FromAi);
        Assert.False(ranked[2].FromAi);
        Assert.Equal("test-key", handler.Last!.Headers.GetValues("x-api-key").Single());
        Assert.Contains("claude-sonnet-5-5", handler.Body);
    }

    [Fact]
    public async Task Only_numbers_and_flags_are_sent_to_the_AI()
    {
        var (service, handler) = Create(true, _ => Claude("[]"));
        await service.RankAsync(Request, [Newcomer, Trusted]);
        Assert.DoesNotContain(Newcomer.Id.ToString(), handler.Body);
        Assert.DoesNotContain(Trusted.Id.ToString(), handler.Body);
        var prompt = SitterMatchingService.BuildPrompt(Request, [Newcomer, Trusted]);
        Assert.Contains("\"id\":\"s1\"", prompt);   // sitters appear only as s1, s2…
        foreach (var word in new[] { "name", "email", "address", "phone", "note", "city" })
            Assert.DoesNotContain("\"" + word, prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("no json here")]
    [InlineData("[{\"id\":")]
    [InlineData("[]")]
    public async Task A_bad_or_empty_answer_falls_back_to_the_rules(string answer)
    {
        var (service, _) = Create(true, _ => Claude(answer));
        var ranked = await service.RankAsync(Request, [Newcomer, Trusted]);
        Assert.Equal([Trusted.Id, Newcomer.Id], ranked.Select(r => r.Id));
        Assert.All(ranked, r => Assert.False(r.FromAi));
    }

    [Fact]
    public async Task Errors_and_timeouts_fall_back_to_the_rules()
    {
        var (failing, _) = Create(true, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        Assert.Equal(Trusted.Id, (await failing.RankAsync(Request, [Newcomer, Trusted]))[0].Id);
        var (slow, _) = Create(true, async r => { await Task.Delay(TimeSpan.FromSeconds(5)); return await Claude("[]"); }, timeout: 1);
        var started = DateTime.UtcNow;
        Assert.Equal(Trusted.Id, (await slow.RankAsync(Request, [Newcomer, Trusted]))[0].Id);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Nothing_is_sent_without_a_key_or_with_one_sitter()
    {
        var (noKey, h1) = Create(true, _ => Claude("[]"), key: null);
        await noKey.RankAsync(Request, [Newcomer, Trusted]);
        var (single, h2) = Create(true, _ => Claude("[]"));
        await single.RankAsync(Request, [Trusted]);
        Assert.Null(h1.Last);
        Assert.Null(h2.Last);
    }

    [Fact]
    public void Long_or_odd_reasons_are_cleaned()
    {
        var picks = SitterMatchingService.ParseAnswer("[{\"id\":\"s1\",\"reason\":\"" + new string('a', 300) + "\"},{\"id\":\"s2\",\"reason\":\"\"}]", [Newcomer, Trusted]);
        Assert.Equal(141, picks[0].Reason.Length);
        Assert.Equal("Good match for your request", picks[1].Reason);
    }
}
