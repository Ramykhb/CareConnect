using CareConnect.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PetSitting.Models;

namespace PetSitting.Services.RequestAssistant;

[Authorize(Roles = Roles.Customer)]
public sealed class RequestAssistantHub(IRequestAssistantService assistant) : Hub
{
    public const string Path = "/hubs/request-assistant";
    private Guid UserId => Guid.TryParse(Context.UserIdentifier, out var id) ? id : throw new HubException("Sign in to use the request assistant.");
    public Task<AssistantSnapshot> Get() => Run(() => assistant.GetAsync(UserId, Context.ConnectionAborted));
    public Task<AssistantSnapshot> Send(Guid conversationId, Guid clientMessageId, string message) =>
        Run(() => assistant.SendAsync(UserId, conversationId, clientMessageId, message, Context.ConnectionAborted));
    public Task<AssistantSnapshot> Reset(Guid conversationId) => Run(() => assistant.ResetAsync(UserId, conversationId, Context.ConnectionAborted));
    private static async Task<AssistantSnapshot> Run(Func<Task<AssistantSnapshot>> action)
    {
        try { return await action(); }
        catch (AssistantException e) { throw new HubException(e.Message); }
    }
}
