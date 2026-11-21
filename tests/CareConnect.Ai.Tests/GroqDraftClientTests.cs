using System.Net;
using System.Text.Json;
using CareConnect.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CareConnect.Ai.Tests;

public sealed class GroqDraftClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request, ct);
    }
    private static GroqDraftClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer, string? key = "test-key") =>
        new(new HttpClient(new Handler(answer)), Options.Create(new GroqAssistantOptions { Enabled = true, ApiKey = key }), NullLogger<GroqDraftClient>.Instance);
    private static readonly AssistantContext Context = new("pet sitting", new(2026, 11, 21, 12, 0, 0, TimeSpan.FromHours(2)), new { pets = new[] { new { key = "p1", name = "Max", species = "Dog" } } }, ["DogWalking", "Boarding"]);
    private static HttpResponseMessage Response(string content, string finish = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish, message = new { content } } } }))
    };

    [Fact]
    public async Task Followups_send_accumulated_fields_and_history_using_Groq_structured_outputs()
    {
        var client = Client(async (request, ct) =>
        {
            Assert.Equal("https://api.groq.com/openai/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("openai/gpt-oss-20b", body.RootElement.GetProperty("model").GetString());
            var schema = body.RootElement.GetProperty("response_format").GetProperty("json_schema");
            Assert.True(schema.GetProperty("strict").GetBoolean());
            var messages = body.RootElement.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Contains("2026-11-21", messages[0].GetProperty("content").GetString());
            Assert.Contains("DogWalking", messages[0].GetProperty("content").GetString());
            Assert.Equal("What time?", messages[2].GetProperty("content").GetString());
            Assert.Equal("3pm for one hour", messages[3].GetProperty("content").GetString());
            return Response(AssistantJson.Serialize(new AssistantExtraction(true, new() { PetKeys = ["p1"], Service = "DogWalking", StartTime = "15:00", EndTime = "16:00" })));
        });
        var result = await client.ExtractAsync(Context, new() { PetKeys = ["p1"], Service = "DogWalking" },
            [new("user", "Walk Max"), new("assistant", "What time?")], "3pm for one hour", default);
        Assert.True(result.InScope);
        Assert.Equal("15:00", result.State.StartTime);
    }

    [Fact]
    public void Schema_requires_all_fields_and_disallows_unknown_actions()
    {
        var schema = GroqDraftClient.Schema();
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var state = schema["properties"]!["state"]!;
        Assert.False(state["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(state["properties"]!.AsObject().Count, state["required"]!.AsArray().Count);
        Assert.DoesNotContain("submit", schema.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ONLY scope", GroqDraftClient.SystemPrompt(Context, new()));
    }

    [Theory]
    [InlineData("not json", "stop")]
    [InlineData("{}", "length")]
    [InlineData("{\"inScope\":true,\"state\":null}", "stop")]
    public async Task Malformed_or_truncated_answers_fail_without_drafting(string content, string finish)
    {
        var client = Client((_, _) => Task.FromResult(Response(content, finish)));
        await Assert.ThrowsAsync<AssistantException>(() => client.ExtractAsync(Context, new(), [], "Walk Max", default));
    }

    [Fact]
    public async Task Provider_failure_is_safe_and_cancellation_propagates()
    {
        var failing = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var error = await Assert.ThrowsAsync<AssistantException>(() => failing.ExtractAsync(Context, new(), [], "Walk Max", default));
        Assert.DoesNotContain("test-key", error.Message);
        var slow = Client(async (_, ct) => { await Task.Delay(10000, ct); return Response("{}"); });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.ExtractAsync(Context, new(), [], "Walk Max", cancellation.Token));
    }

    [Fact]
    public async Task Missing_key_never_sends_a_request()
    {
        var client = Client((_, _) => throw new InvalidOperationException("Must not call provider"), key: null);
        Assert.False(client.Available);
        await Assert.ThrowsAsync<AssistantException>(() => client.ExtractAsync(Context, new(), [], "Walk Max", default));
    }
}
