using System.Text.Json;
using CareConnect.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.RequestAssistant;
using PetSitting.ViewModels;
using Xunit;

namespace PetSitting.Tests;

public sealed class RequestAssistantPersistenceTests
{
    private static readonly Guid UserId = Guid.NewGuid(), PetId = Guid.NewGuid();
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2030, 1, 2, 8, 0, 0, TimeSpan.Zero); }
    private sealed class Provider(Func<AssistantContext, AssistantState, IReadOnlyList<AssistantTurn>, string, AssistantExtraction> answer) : IGroqDraftClient
    {
        public bool Available => true;
        public int Calls;
        public Task<AssistantExtraction> ExtractAsync(AssistantContext context, AssistantState state, IReadOnlyList<AssistantTurn> history, string message, CancellationToken ct)
        { Calls++; return Task.FromResult(answer(context, state, history, message)); }
    }
    private static async Task Run(Func<PetSittingDbContext, Task> test)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development").ConfigureServices(s => s.RemoveAll<IHostedService>()));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Users.Add(new ApplicationUser { Id = UserId, FirstName = "Assistant", LastName = "Test" });
            db.Pets.Add(new Pet { Id = PetId, OwnerId = UserId, Name = "Max", Species = "Dog" });
            await db.SaveChangesAsync();
            await test(db);
        });
    }
    private static RequestAssistantService Service(PetSittingDbContext db, Provider provider) => new(db, provider, new AssistantThrottle(new Clock()), new Clock());
    private static AssistantState Complete()
    {
        var state = new AssistantState { Service = "DogWalking", StartDate = "2030-01-03", EndDate = "2030-01-03", StartTime = "14:00", EndTime = "15:00", City = "Beirut", StreetAddress = "Cedar Street", Budget = 50 };
        state.PetKeys = ["pet_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(PetId.ToByteArray()))[..16]];
        return state;
    }

    [SqlServerFact]
    public async Task Followups_keep_memory_and_create_one_owned_review_draft_without_submitting()
    {
        await Run(async db =>
        {
            var provider = new Provider((context, state, history, message) =>
            {
                using var profile = JsonDocument.Parse(AssistantJson.Serialize(context.Profile));
                state.PetKeys = [profile.RootElement.GetProperty("pets")[0].GetProperty("key").GetString()!];
                if (message == "Walk tomorrow")
                {
                    state.Service = "DogWalking"; state.StartDate = state.EndDate = "2030-01-03";
                    state.StartTime = "14:00"; state.EndTime = "15:00"; state.City = "Beirut"; state.Budget = 50;
                    Assert.Contains(history, t => t.Role == "user" && t.Content == "Who needs care");
                }
                if (message == "Cedar Street") { state.StreetAddress = message; Assert.Equal(5, history.Count); }
                return new(true, state);
            });
            var service = Service(db, provider);
            var first = await service.GetAsync(UserId);
            var one = await service.SendAsync(UserId, first.ConversationId, Guid.NewGuid(), "Who needs care");
            Assert.Null(one.DraftUrl);
            var two = await service.SendAsync(UserId, first.ConversationId, Guid.NewGuid(), "Walk tomorrow");
            Assert.Null(two.DraftUrl); Assert.Contains("street address", two.Messages.Last().Content);
            // A new service and a fresh EF tracker must recover the accumulated conversation from SQL.
            db.ChangeTracker.Clear(); service = Service(db, provider);
            var restored = await service.GetAsync(UserId); Assert.Equal(5, restored.Messages.Count);
            var messageId = Guid.NewGuid();
            var final = await service.SendAsync(UserId, restored.ConversationId, messageId, "Cedar Street");
            Assert.NotNull(final.DraftUrl); Assert.Contains("No request has been sent", final.Messages.Last().Content);
            var draft = await db.OwnerRequestDrafts.SingleAsync(d => d.CustomerId == UserId);
            var form = JsonSerializer.Deserialize<OwnerRequestForm>(draft.DataJson)!;
            Assert.Equal(6, draft.Step); Assert.Null(draft.SubmittedRequestId); Assert.False(form.CareConfirmed);
            Assert.Equal(50, form.Budget); Assert.Equal("Cedar Street", form.StreetAddress);
            Assert.Equal(new[] { PetId }, form.PetIds);
            var duplicate = await service.SendAsync(UserId, restored.ConversationId, messageId, "Cedar Street");
            Assert.Equal(final.DraftUrl, duplicate.DraftUrl); Assert.Equal(3, provider.Calls);
            Assert.Equal(1, await db.OwnerRequestDrafts.CountAsync(d => d.CustomerId == UserId));
            var other = new ApplicationUser { FirstName = "Other", LastName = "Owner" }; db.Users.Add(other); await db.SaveChangesAsync();
            var privateChat = await service.GetAsync(other.Id); Assert.Null(privateChat.DraftUrl);
            Assert.DoesNotContain(privateChat.Messages, t => t.Content == "Cedar Street");
            var reset = await service.ResetAsync(UserId, restored.ConversationId);
            Assert.NotEqual(restored.ConversationId, reset.ConversationId); Assert.Single(reset.Messages);
            Assert.True(await db.OwnerRequestDrafts.AnyAsync(d => d.Id == draft.Id));
            await Assert.ThrowsAsync<AssistantException>(() => service.SendAsync(UserId, restored.ConversationId, Guid.NewGuid(), "stale tab"));
        });
    }

    [SqlServerFact]
    public async Task Outside_scope_and_provider_failures_cannot_create_or_change_a_draft()
    {
        await Run(async db =>
        {
            var provider = new Provider((_, _, _, _) => new(false, Complete()));
            var service = Service(db, provider); var first = await service.GetAsync(UserId);
            var rejected = await service.SendAsync(UserId, first.ConversationId, Guid.NewGuid(), "Write code for me");
            Assert.Contains("only help draft", rejected.Messages.Last().Content); Assert.Empty(rejected.Fields);
            Assert.False(await db.OwnerRequestDrafts.AnyAsync(d => d.CustomerId == UserId));
            var failing = Service(db, new((_, _, _, _) => throw new AssistantException("Provider unavailable")));
            await Assert.ThrowsAsync<AssistantException>(() => failing.SendAsync(UserId, first.ConversationId, Guid.NewGuid(), "Need care"));
            var unchanged = await service.GetAsync(UserId); Assert.Equal(rejected.Messages.Count, unchanged.Messages.Count);
            await Assert.ThrowsAsync<AssistantException>(() => service.SendAsync(UserId, first.ConversationId, Guid.NewGuid(), new string('x', 1501)));
        });
    }

    [SqlServerFact]
    public async Task Invalid_app_specific_details_never_create_a_draft()
    {
        await Run(async db =>
        {
            var state = Complete();
            state.PetKeys = ["foreign-pet-key"];
            var question = "Which of your pets";
            var service = Service(db, new((_, _, _, _) => new(true, state)));
            var first = await service.GetAsync(UserId);
            var result = await service.SendAsync(UserId, first.ConversationId, Guid.NewGuid(), "Draft this");
            Assert.Null(result.DraftUrl); Assert.Contains(question, result.Messages.Last().Content);
            Assert.False(await db.OwnerRequestDrafts.AnyAsync(d => d.CustomerId == UserId));
        });
    }

    [SqlServerFact]
    public async Task Concurrent_turns_cannot_insert_duplicate_drafts()
    {
        await Run(async db =>
        {
            var first = Service(db, new((_, _, _, _) => new(true, Complete())));
            var snapshot = await first.GetAsync(UserId);
            var options = new DbContextOptionsBuilder<PetSittingDbContext>().UseSqlServer(db.Database.GetDbConnection()).Options;
            await using var otherDb = new PetSittingDbContext(options);
            await otherDb.Database.UseTransactionAsync(db.Database.CurrentTransaction!.GetDbTransaction());
            var second = Service(otherDb, new((_, _, _, _) => new(true, Complete())));
            await second.GetAsync(UserId);
            Assert.NotNull((await first.SendAsync(UserId, snapshot.ConversationId, Guid.NewGuid(), "Complete request")).DraftUrl);
            await Assert.ThrowsAsync<AssistantException>(() => second.SendAsync(UserId, snapshot.ConversationId, Guid.NewGuid(), "Another turn"));
            Assert.Equal(1, await db.OwnerRequestDrafts.CountAsync(d => d.CustomerId == UserId));
        });
    }

    [SqlServerFact]
    public async Task Expired_conversation_memory_is_cleared()
    {
        await Run(async db =>
        {
            var service = Service(db, new((_, state, _, _) => new(true, state)));
            var first = await service.GetAsync(UserId);
            await service.SendAsync(UserId, first.ConversationId, Guid.NewGuid(), "Remember this");
            var stored = await db.RequestAssistantConversations.SingleAsync(c => c.UserId == UserId);
            stored.UpdatedAtUtc = new Clock().GetUtcNow().AddHours(-25); await db.SaveChangesAsync();
            var reset = await service.GetAsync(UserId);
            Assert.NotEqual(first.ConversationId, reset.ConversationId); Assert.Single(reset.Messages);
        });
    }
}
