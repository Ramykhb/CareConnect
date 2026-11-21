using System.Net;
using CareConnect.Ai;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PetSitting.Models;
using Xunit;

namespace PetSitting.Tests;

[Route("__tests/request-assistant")]
public sealed class AssistantWidgetPreviewController : Controller
{
    [HttpGet] public IActionResult Preview() => PartialView("/Views/Shared/_RequestAssistant.cshtml");
}

public sealed class RequestAssistantSocketTests
{
    private sealed class Memory : IRequestAssistantService
    {
        private readonly Dictionary<Guid, AssistantSnapshot> sessions = [];
        public Guid LastUser;
        public Task<AssistantSnapshot> GetAsync(Guid userId, CancellationToken ct = default)
        {
            LastUser = userId;
            if (!sessions.TryGetValue(userId, out var snapshot)) sessions[userId] = snapshot = new(Guid.NewGuid(), [new("assistant", "Which pet or child needs care?")], [], null, true);
            return Task.FromResult(snapshot);
        }
        public async Task<AssistantSnapshot> SendAsync(Guid userId, Guid conversationId, Guid clientMessageId, string message, CancellationToken ct = default)
        {
            var old = await GetAsync(userId, ct);
            if (old.ConversationId != conversationId) throw new AssistantException("Chat changed.");
            return sessions[userId] = old with { Messages = old.Messages.Concat(new AssistantTurn[] { new("user", message, clientMessageId), new("assistant", "When do you need care?") }).ToArray() };
        }
        public Task<AssistantSnapshot> ResetAsync(Guid userId, Guid conversationId, CancellationToken ct = default) { sessions.Remove(userId); return GetAsync(userId, ct); }
    }

    private static WebApplicationFactory<Program> Factory(Memory memory) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.UseEnvironment("Development").ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.AddControllersWithViews().AddApplicationPart(typeof(AssistantWidgetPreviewController).Assembly);
            services.RemoveAll<IRequestAssistantService>(); services.AddSingleton<IRequestAssistantService>(memory);
            services.AddAuthentication(o => o.DefaultAuthenticateScheme = "AssistantSocketTests")
                .AddScheme<AuthenticationSchemeOptions, DashboardTests.DashboardAuthentication>("AssistantSocketTests", _ => { });
        }));

    private static HubConnection Connect(WebApplicationFactory<Program> factory) => new HubConnectionBuilder()
        .WithUrl("http://localhost/hubs/request-assistant", o =>
        {
            o.Transports = HttpTransportType.LongPolling;
            o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            o.Headers.Add("X-Dashboard-Test-Role", Roles.Customer);
        }).Build();

    [Fact]
    public async Task Socket_sends_followups_and_restores_history_after_reconnecting()
    {
        var memory = new Memory(); await using var factory = Factory(memory);
        await using (var connection = Connect(factory))
        {
            await connection.StartAsync();
            var first = await connection.InvokeAsync<AssistantSnapshot>("Get");
            var next = await connection.InvokeAsync<AssistantSnapshot>("Send", first.ConversationId, Guid.NewGuid(), "Tomorrow");
            Assert.Equal(3, next.Messages.Count);
            Assert.Equal("Tomorrow", next.Messages[1].Content);
            Assert.NotEqual(Guid.Empty, memory.LastUser);
        }
        await using var restored = Connect(factory); await restored.StartAsync();
        var history = await restored.InvokeAsync<AssistantSnapshot>("Get");
        Assert.Equal("Tomorrow", history.Messages[1].Content);
        var reset = await restored.InvokeAsync<AssistantSnapshot>("Reset", history.ConversationId);
        Assert.NotEqual(history.ConversationId, reset.ConversationId);
        Assert.Single(reset.Messages);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Roles.Sitter)]
    public async Task Only_customers_can_open_the_socket(string? role)
    {
        await using var factory = Factory(new()); using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", role);
        using var response = await client.PostAsync("/hubs/request-assistant/negotiate?negotiateVersion=1", new StringContent(""));
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Redirect });
    }

    [Fact]
    public async Task Foreign_browser_origins_cannot_use_the_socket()
    {
        await using var factory = Factory(new()); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", Roles.Customer);
        client.DefaultRequestHeaders.Add("Origin", "https://unrelated.example");
        using var response = await client.PostAsync("/hubs/request-assistant/negotiate?negotiateVersion=1", new StringContent(""));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(Roles.Customer, true)]
    [InlineData(Roles.Sitter, false)]
    [InlineData(null, false)]
    public async Task Floating_widget_is_only_rendered_for_customers(string? role, bool visible)
    {
        await using var factory = Factory(new()); using var client = factory.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Add("X-Dashboard-Test-Role", role);
        var html = await client.GetStringAsync("/__tests/request-assistant");
        Assert.Equal(visible, html.Contains("data-request-assistant"));
        if (visible) { Assert.Contains("Review draft", html); Assert.Contains("24 hours", html); Assert.DoesNotContain("gsk_", html); }
    }
}
