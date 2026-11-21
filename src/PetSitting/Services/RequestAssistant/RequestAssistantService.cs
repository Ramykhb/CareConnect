using System.Text.Json;
using CareConnect.Ai;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Scheduling;
using PetSitting.ViewModels;

namespace PetSitting.Services.RequestAssistant;

public sealed class RequestAssistantService(PetSittingDbContext db, IGroqDraftClient groq, AssistantThrottle throttle, TimeProvider clock) : IRequestAssistantService
{
    private static readonly string[] Services = Enum.GetNames<ServiceType>();
    private DateTimeOffset LocalNow => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), ScheduleMath.Zone);
    private const string ScopeReply = "I can only help draft a pet sitting care request. Tell me who needs care, the service, dates, location or budget.";

    public async Task<AssistantSnapshot> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await CheckAccount(userId, ct);
        return await Snapshot(await Load(userId, ct), ct);
    }

    public async Task<AssistantSnapshot> SendAsync(Guid userId, Guid conversationId, Guid clientMessageId, string message, CancellationToken ct = default)
    {
        await CheckAccount(userId, ct);
        if (clientMessageId == Guid.Empty || string.IsNullOrWhiteSpace(message) || message.Length > 1500)
            throw new AssistantException("Write a message between 1 and 1,500 characters.");
        var conversation = await Load(userId, ct);
        if (conversation.SessionId != conversationId) throw new AssistantException("This chat changed in another tab. Reopen it to load the latest conversation.");
        var history = AssistantJson.Read<List<AssistantTurn>>(conversation.MessagesJson);
        if (history.Any(t => t.ClientMessageId == clientMessageId)) return await Snapshot(conversation, ct);
        if (conversation.DraftId is not null) throw new AssistantException("Your draft is ready. Open it to edit, or start a new chat for another request.");
        if (history.Count >= 40) throw new AssistantException("This chat has reached its limit. Start a new chat to draft a request.");
        throttle.Check(userId);
        var state = AssistantJson.Read<AssistantState>(conversation.StateJson);
        var result = await groq.ExtractAsync(await Context(userId, ct), state, history, message.Trim(), ct);
        string reply;
        if (!result.InScope) reply = ScopeReply;
        else
        {
            state = result.State;
            var questions = await Questions(userId, state, ct);
            if (questions.Count == 0)
            {
                var draft = await BuildDraft(userId, state, ct);
                db.OwnerRequestDrafts.Add(draft);
                conversation.DraftId = draft.Id;
                reply = "Your draft is ready. Open it to review all details and submit it yourself. No request has been sent to sitters.";
            }
            else reply = string.Join(" ", questions.Take(2));
            conversation.StateJson = AssistantJson.Serialize(state);
        }
        history.Add(new("user", message.Trim(), clientMessageId));
        history.Add(new("assistant", reply));
        conversation.MessagesJson = AssistantJson.Serialize(history);
        conversation.UpdatedAtUtc = clock.GetUtcNow();
        // Conversation update and draft insertion commit together. RowVersion rejects overlapping turns/tabs.
        await Save(ct);
        return await Snapshot(conversation, ct);
    }

    public async Task<AssistantSnapshot> ResetAsync(Guid userId, Guid conversationId, CancellationToken ct = default)
    {
        await CheckAccount(userId, ct);
        var conversation = await Load(userId, ct);
        if (conversation.SessionId != conversationId) throw new AssistantException("This chat changed in another tab. Reopen it before starting a new chat.");
        await Clear(conversation, ct);
        await Save(ct);
        return await Snapshot(conversation, ct);
    }

    private async Task CheckAccount(Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive, ct))
            throw new AssistantException("Sign in with an active customer account to use the request assistant.");
    }

    private async Task<RequestAssistantConversation> Load(Guid userId, CancellationToken ct)
    {
        var conversation = await db.RequestAssistantConversations.SingleOrDefaultAsync(c => c.UserId == userId, ct);
        if (conversation is null)
        {
            conversation = new() { UserId = userId, UpdatedAtUtc = clock.GetUtcNow() };
            await Clear(conversation, ct);
            db.RequestAssistantConversations.Add(conversation);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException e) when (e.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
            {
                db.ChangeTracker.Clear();
                conversation = await db.RequestAssistantConversations.SingleAsync(c => c.UserId == userId, ct);
            }
        }
        if (conversation.UpdatedAtUtc < clock.GetUtcNow().AddHours(-24))
        {
            await Clear(conversation, ct);
            await Save(ct);
        }
        return conversation;
    }

    private async Task Clear(RequestAssistantConversation conversation, CancellationToken ct)
    {
        conversation.SessionId = Guid.NewGuid();
        conversation.DraftId = null;
        conversation.StateJson = AssistantJson.Serialize(await Seed(conversation.UserId, ct));
        conversation.MessagesJson = AssistantJson.Serialize(new[] { new AssistantTurn("assistant",
            "Hi! I can help draft your pet sitting request. Tell me what care you need, and I'll ask for any missing details. Dates and times use Beirut time.") });
        conversation.UpdatedAtUtc = clock.GetUtcNow();
    }

    private async Task Save(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new AssistantException("This chat changed in another tab. Reopen it to load the latest conversation before retrying.");
        }
    }

    private async Task<AssistantSnapshot> Snapshot(RequestAssistantConversation conversation, CancellationToken ct)
    {
        var state = AssistantJson.Read<AssistantState>(conversation.StateJson);
        var fields = AssistantValidation.Fields(state);
        var pets = await db.Pets.AsNoTracking().Where(p => p.OwnerId == conversation.UserId).ToListAsync(ct);
        var names = pets.Where(p => state.PetKeys.Contains(PetKey(p.Id))).Select(p => p.Name).ToArray();
        if (names.Length > 0) fields.Insert(0, new("Pets", string.Join(", ", names)));
        // Never return a link to a missing or someone else's draft.
        var hasDraft = conversation.DraftId is { } draftId && await db.OwnerRequestDrafts.AsNoTracking().AnyAsync(d => d.Id == draftId && d.CustomerId == conversation.UserId, ct);
        return new(conversation.SessionId, AssistantJson.Read<List<AssistantTurn>>(conversation.MessagesJson), fields,
            hasDraft ? "/CareRequests/Create?draftId=" + conversation.DraftId : null, groq.Available);
    }

    private async Task<AssistantContext> Context(Guid userId, CancellationToken ct)
    {
        var pets = await db.Pets.AsNoTracking().Where(p => p.OwnerId == userId).OrderBy(p => p.Id).ToListAsync(ct);
        return new("pet sitting", LocalNow, new { pets = pets.Select(p => new { key = PetKey(p.Id), p.Name, p.Species }) }, Services);
    }

    private Task<AssistantState> Seed(Guid userId, CancellationToken ct) => Task.FromResult(new AssistantState());

    private async Task<List<string>> Questions(Guid userId, AssistantState state, CancellationToken ct)
    {
        var questions = new List<string>();
        var validation = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if (state.PetKeys is null || state.PetKeys.Length > 20 ||
            !System.ComponentModel.DataAnnotations.Validator.TryValidateObject(state, new(state), validation, true))
            throw new AssistantException("Some extracted details are too long or invalid. Please shorten them and try again.");
        var pets = await db.Pets.AsNoTracking().Where(p => p.OwnerId == userId).ToListAsync(ct);
        var selected = pets.Where(p => state.PetKeys.Contains(PetKey(p.Id))).ToList();
        if (pets.Count == 0) questions.Add("Add a pet in My pets first, then return here to draft your request.");
        else if (selected.Count == 0 || selected.Count != state.PetKeys.Distinct().Count())
            questions.Add("Which of your pets needs care: " + string.Join(", ", pets.Select(p => p.Name)) + "?");
        if (state.Service == "DogWalking" && selected.Any(p => p.Species != "Dog")) questions.Add("Dog walking is only for dogs. Which dog needs care, or would you like another service?");
        questions.AddRange(AssistantValidation.Questions(state, Services, state.Service == "Boarding", clock.GetUtcNow(), ScheduleMath.Zone));
        return questions;
    }

    private async Task<OwnerRequestDraft> BuildDraft(Guid userId, AssistantState state, CancellationToken ct)
    {
        AssistantValidation.Date(state.StartDate, out var start); AssistantValidation.Date(state.EndDate, out var end);
        AssistantValidation.Time(state.StartTime, out var from); AssistantValidation.Time(state.EndTime, out var to);
        var form = new OwnerRequestForm
        {
            PetIds = await db.Pets.AsNoTracking().Where(p => p.OwnerId == userId).Select(p => p.Id).ToListAsync(ct),
            ServiceType = Enum.Parse<ServiceType>(state.Service!), StartDate = start, EndDate = end, StartTime = from, EndTime = to,
            City = state.City!.Trim(), StreetAddress = state.StreetAddress?.Trim(), Instructions = state.Instructions,
            Budget = state.Budget, CompareOffers = state.CompareOffers ?? true, CareConfirmed = false
        };
        form.PetIds = form.PetIds.Where(id => state.PetKeys.Contains(PetKey(id))).ToList();
        return new() { CustomerId = userId, DataJson = JsonSerializer.Serialize(form), Step = 6, UpdatedAtUtc = clock.GetUtcNow() };
    }
    private static string PetKey(Guid id) => "pet_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(id.ToByteArray()))[..16];
}
