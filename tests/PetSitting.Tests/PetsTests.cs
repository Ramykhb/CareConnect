using System.ComponentModel.DataAnnotations;
using System.Net;
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

public sealed class PetsTests
{
    private static WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

    [Theory]
    [InlineData("/Pets")]
    [InlineData("/Pets/Create")]
    public async Task My_pets_requires_a_signed_in_owner(string path)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Identity/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public void Pet_form_rejects_missing_name_species_and_invalid_age()
    {
        var form = new PetFormViewModel { Name = "", Species = "", AgeYears = 150 };
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(form, new ValidationContext(form), results, validateAllProperties: true));
        var invalid = results.SelectMany(r => r.MemberNames).ToHashSet();
        Assert.Contains(nameof(PetFormViewModel.Name), invalid);
        Assert.Contains(nameof(PetFormViewModel.Species), invalid);
        Assert.Contains(nameof(PetFormViewModel.AgeYears), invalid);
    }

    [Fact]
    public void Pet_form_accepts_a_complete_pet()
    {
        var form = new PetFormViewModel { Name = "Max", Species = "Dog", Breed = "Golden Retriever", AgeYears = 3 };
        Assert.True(Validator.TryValidateObject(form, new ValidationContext(form), new List<ValidationResult>(), validateAllProperties: true));
    }

    // Uses the local SQL Server (CARECONNECT_SQL_TESTS=1); data is rolled back.
    [SqlServerFact]
    public async Task Repository_only_returns_and_deletes_the_owners_own_pets()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        var repository = new PetRepository(db);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var owner = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Owner", LastName = "One" };
            var other = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Owner", LastName = "Two" };
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync();

            var pet = new Pet { OwnerId = owner.Id, Name = "Max", Species = "Dog" };
            await repository.AddAsync(pet);

            Assert.Single(await repository.GetAllForOwnerAsync(owner.Id, null, null));
            Assert.Empty(await repository.GetAllForOwnerAsync(other.Id, null, null));
            Assert.Null(await repository.GetByIdAsync(pet.Id, other.Id));
            Assert.False(await repository.DeleteAsync(pet.Id, other.Id));
            Assert.True(await repository.DeleteAsync(pet.Id, owner.Id));
            Assert.Null(await repository.GetByIdAsync(pet.Id, owner.Id));
        });
    }
}
