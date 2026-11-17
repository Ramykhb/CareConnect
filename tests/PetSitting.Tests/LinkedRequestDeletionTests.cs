using System.Text.Json;
using CareConnect.Contracts.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Repositories;
using Xunit;

namespace PetSitting.Tests;

public sealed class LinkedRequestDeletionTests
{
    [SqlServerFact]
    public async Task Deleting_an_unbooked_combined_request_preserves_its_link_and_queues_cancellation()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development").ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["RabbitMq:Enabled"] = "false" })));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        // Cover both an invitation waiting to be claimed and an established request pair.
        foreach (var paired in new[] { false, true })
        {
            var owner = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Linked", LastName = "Test", UserName = "linked-" + Guid.NewGuid().ToString("N") };
            var request = new PetCareRequest { Customer = owner, Title = "Combined care", Description = "Test", City = "Beirut",
                StartsAtUtc = DateTimeOffset.UtcNow.AddDays(3), EndsAtUtc = DateTimeOffset.UtcNow.AddDays(3).AddHours(2) };
            var pet = new Pet { Owner = owner, Name = "Dog", Species = "Dog" };
            request.Pets.Add(new PetCareRequestPet { Pet = pet });
            var link = new CombinedCareLink { LocalRequestId = request.Id, PeerRequestId = paired ? Guid.NewGuid() : null,
                OriginPlatform = "PetSitting", InvitationHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                InvitationExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) };
            var draft = new OwnerRequestDraft { Customer = owner, SubmittedRequest = request };
            try
            {
                db.AddRange(owner, request, link, draft);
                await db.SaveChangesAsync();
                var version = Convert.ToBase64String(request.RowVersion);
                var repository = new PetCareRequestRepository(db, TimeProvider.System);
                Assert.Equal(RequestWriteResult.NotFound, await repository.DeleteAsync(Guid.NewGuid(), request.Id, version));
                Assert.Equal(RequestWriteResult.Conflict, await repository.DeleteAsync(owner.Id, request.Id, Convert.ToBase64String(new byte[8])));
                Assert.False(await db.MessageOutboxEntries.AnyAsync(m => m.CorrelationId == link.Id));

                Assert.Equal(RequestWriteResult.Cancelled, await repository.DeleteAsync(owner.Id, request.Id, version));
                db.ChangeTracker.Clear();
                Assert.Equal(RequestStatus.Cancelled, (await db.PetCareRequests.SingleAsync(r => r.Id == request.Id)).Status);
                Assert.True(await db.PetCareRequestPets.AnyAsync(p => p.PetCareRequestId == request.Id && p.PetId == pet.Id));
                Assert.True(await db.OwnerRequestDrafts.AnyAsync(d => d.Id == draft.Id && d.SubmittedRequestId == request.Id));
                var savedLink = await db.CombinedCareLinks.SingleAsync(l => l.Id == link.Id);
                Assert.Equal(request.Id, savedLink.LocalRequestId);
                Assert.Equal(link.PeerRequestId, savedLink.PeerRequestId);
                Assert.Equal(1L, savedLink.LocalRevision);
                var outgoing = await db.MessageOutboxEntries.SingleAsync(m => m.CorrelationId == link.Id);
                Assert.Null(outgoing.PublishedAtUtc);
                var message = JsonSerializer.Deserialize<CombinedCareEvent>(outgoing.PayloadJson)!;
                Assert.Equal("State", message.Metadata.EventType);
                Assert.Equal(request.Id, message.RequestId);
                Assert.Equal(link.PeerRequestId, message.PeerRequestId);
                Assert.Equal("Cancelled", message.RequestStatus);
                Assert.Null(message.BookingStatus);
                Assert.Equal(savedLink.LocalRevision, message.Revision);
            }
            finally
            {
                db.ChangeTracker.Clear();
                await db.MessageOutboxEntries.Where(m => m.CorrelationId == link.Id).ExecuteDeleteAsync();
                await db.CombinedCareLinks.Where(l => l.Id == link.Id).ExecuteDeleteAsync();
                await db.OwnerRequestDrafts.Where(d => d.CustomerId == owner.Id).ExecuteDeleteAsync();
                await db.PetCareRequestPets.Where(p => p.PetCareRequestId == request.Id).ExecuteDeleteAsync();
                await db.PetCareRequests.Where(r => r.Id == request.Id).ExecuteDeleteAsync();
                await db.Pets.Where(p => p.Id == pet.Id).ExecuteDeleteAsync();
                await db.Users.Where(u => u.Id == owner.Id).ExecuteDeleteAsync();
            }
        }
    }
}
