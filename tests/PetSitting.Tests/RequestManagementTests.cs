using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Repositories;
using PetSitting.ViewModels;
using Xunit;

namespace PetSitting.Tests;

public sealed class RequestManagementTests
{
    private static RequestEditViewModel Form() => new()
    {
        Version = Convert.ToBase64String(new byte[8]), Title = "Walk", Description = "An afternoon walk", City = "Beirut",
        ServiceType = ServiceType.DogWalking, PetIds = [Guid.NewGuid()], Budget = 20, ServiceAddress = "Private street",
        StartsAtLocal = DashboardViewModel.Local(DateTimeOffset.UtcNow.AddDays(3)).DateTime,
        EndsAtLocal = DashboardViewModel.Local(DateTimeOffset.UtcNow.AddDays(3).AddHours(2)).DateTime
    };

    [Fact]
    public void Edit_validates_dates_pets_service_and_private_address()
    {
        var form = Form();
        var errors = new List<ValidationResult>();
        Assert.True(Validator.TryValidateObject(form, new(form), errors, true));
        form.EndsAtLocal = form.StartsAtLocal;
        Assert.Contains(form.Validate(new(form)), e => e.MemberNames.Contains(nameof(form.EndsAtLocal)));
        form = Form(); form.PetIds.Add(form.PetIds[0]);
        Assert.Contains(form.Validate(new(form)), e => e.MemberNames.Contains(nameof(form.PetIds)));
        form = Form(); form.ServiceAddress = " ";
        Assert.Contains(form.Validate(new(form)), e => e.MemberNames.Contains(nameof(form.ServiceAddress)));
        form.ServiceType = ServiceType.Boarding;
        Assert.DoesNotContain(form.Validate(new(form)), e => e.MemberNames.Contains(nameof(form.ServiceAddress)));
        form = Form(); form.ServiceType = (ServiceType)999;
        Assert.False(Validator.TryValidateObject(form, new(form), [], true));
    }

    [Fact]
    public void Only_future_open_requests_can_be_edited()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new PetCareRequest { StartsAtUtc = now.AddDays(1) };
        Assert.True(PetCareRequestRepository.IsEditable(request, now));
        foreach (var status in Enum.GetValues<RequestStatus>().Where(s => s != RequestStatus.Open))
        {
            request.Status = status;
            Assert.False(PetCareRequestRepository.IsEditable(request, now));
        }
        request.Status = RequestStatus.Open; request.StartsAtUtc = now;
        Assert.False(PetCareRequestRepository.IsEditable(request, now));
    }

    [SqlServerFact]
    public async Task Owner_edits_open_requests_with_concurrency_and_deletes_the_draft_and_pet_links()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        var owner = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Request", LastName = "Test", UserName = "crud-" + Guid.NewGuid().ToString("N") };
        var pet = new Pet { Owner = owner, Name = "Dog", Species = "Dog" };
        var request = new PetCareRequest { Customer = owner, Title = "Original", Description = "Walk", City = "Beirut", Budget = 10,
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(3), EndsAtUtc = DateTimeOffset.UtcNow.AddDays(3).AddHours(1),
            Pets = [new() { Pet = pet }] };
        try
        {
            db.AddRange(owner, pet, request, new OwnerRequestDraft { Customer = owner, SubmittedRequest = request });
            await db.SaveChangesAsync();
            var repo = new PetCareRequestRepository(db, TimeProvider.System);
            var form = Form(); form.PetIds = [pet.Id]; form.Version = Convert.ToBase64String(request.RowVersion);
            request.Status = RequestStatus.Booked; await db.SaveChangesAsync();
            Assert.Equal(RequestWriteResult.NotEditable, await repo.UpdateAsync(owner.Id, request.Id, form));
            Assert.Equal(RequestWriteResult.NotEditable, await repo.DeleteAsync(owner.Id, request.Id, form.Version));
            var tracked = await db.PetCareRequests.SingleAsync(r => r.Id == request.Id);
            tracked.Status = RequestStatus.Open; await db.SaveChangesAsync();
            form.Version = Convert.ToBase64String(tracked.RowVersion);
            Assert.Equal(RequestWriteResult.NotFound, await repo.UpdateAsync(Guid.NewGuid(), request.Id, form));
            form.PetIds = [Guid.NewGuid()];
            Assert.Equal(RequestWriteResult.InvalidPets, await repo.UpdateAsync(owner.Id, request.Id, form));
            form.PetIds = [pet.Id];
            Assert.Equal(RequestWriteResult.Saved, await repo.UpdateAsync(owner.Id, request.Id, form));
            Assert.Equal(RequestWriteResult.Conflict, await repo.UpdateAsync(owner.Id, request.Id, form));
            Assert.Equal(RequestWriteResult.Conflict, await repo.DeleteAsync(owner.Id, request.Id, form.Version));
            var updated = (await repo.FindAsync(owner.Id, request.Id))!;
            Assert.Equal("Walk", updated.Title); Assert.Equal(20, updated.Budget);
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<OwnerRequestForm>((await db.OwnerRequestDrafts.AsNoTracking().SingleAsync(d => d.SubmittedRequestId == request.Id)).DataJson)!;
            Assert.Equal(form.ServiceAddress, snapshot.Address); Assert.Equal(form.Description, snapshot.Instructions);
            Assert.Equal(RequestWriteResult.NotFound, await repo.DeleteAsync(Guid.NewGuid(), request.Id, Convert.ToBase64String(updated.RowVersion)));
            Assert.Equal(RequestWriteResult.Deleted, await repo.DeleteAsync(owner.Id, request.Id, Convert.ToBase64String(updated.RowVersion)));
            Assert.False(await db.OwnerRequestDrafts.AnyAsync(d => d.SubmittedRequestId == request.Id));
            Assert.False(await db.PetCareRequestPets.AnyAsync(p => p.PetCareRequestId == request.Id));
            Assert.True(await db.Pets.AnyAsync(p => p.Id == pet.Id));
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.OwnerRequestDrafts.Where(d => d.CustomerId == owner.Id).ExecuteDeleteAsync();
            await db.PetCareRequestPets.Where(p => p.PetCareRequestId == request.Id).ExecuteDeleteAsync();
            await db.PetCareRequests.Where(r => r.Id == request.Id).ExecuteDeleteAsync();
            await db.Pets.Where(p => p.OwnerId == owner.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id).ExecuteDeleteAsync();
        }
    }
}
