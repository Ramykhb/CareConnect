using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Repositories;
using PetSitting.Services.Auth;
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

    [SqlServerFact]
    public async Task Owner_can_browse_filter_sort_and_edit_pet_care_profiles()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"pets-{Guid.NewGuid():N}@example.test";
        var petId = Guid.NewGuid();
        Guid ownerId = default;
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var registration = scope.ServiceProvider.GetRequiredService<RegistrationService>();
                var (result, owner) = await registration.RegisterAsync(email, "Test!Password123", "Pet", "Owner", "Customer");
                Assert.True(result.Succeeded);
                ownerId = owner!.Id;
                owner.EmailConfirmed = true;
                Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().UpdateAsync(owner)).Succeeded);
                var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
                db.Pets.AddRange(
                    new Pet { Id = petId, OwnerId = ownerId, Name = "Max", Species = "Dog", Breed = "Golden Retriever", AgeYears = 3, MedicalNotes = "No allergies.", FeedingInstructions = "One cup twice daily.", CareInstructions = "Keep on a lead." },
                    new Pet { OwnerId = ownerId, Name = "Luna", Species = "Cat", AgeYears = 5 });
                await db.SaveChangesAsync();
            }
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Identity/Account/Login", new()
            { ["Input.Email"] = email, ["Input.Password"] = "Test!Password123" })).StatusCode);
            var list = await client.GetStringAsync("/Pets");
            Assert.Contains("Max", list);
            Assert.Contains("Luna", list);
            Assert.Contains("Care details ready", list);
            Assert.Contains("Complete Luna", list);
            Assert.True(list.IndexOf(">Luna</a>", StringComparison.Ordinal) < list.IndexOf(">Max</a>", StringComparison.Ordinal));
            var sorted = await client.GetStringAsync("/Pets?sort=age");
            Assert.True(sorted.IndexOf(">Max</a>", StringComparison.Ordinal) < sorted.IndexOf(">Luna</a>", StringComparison.Ordinal));
            var filtered = await client.GetStringAsync("/Pets?species=Cat&search=Luna&sort=age");
            Assert.Contains(">Luna</a>", filtered);
            Assert.DoesNotContain(">Max</a>", filtered);
            Assert.Contains("No pets match your search", await client.GetStringAsync("/Pets?search=unknown"));
            var details = await client.GetStringAsync($"/Pets/Details/{petId}");
            Assert.Contains("No allergies.", details);
            Assert.Contains("One cup twice daily.", details);
            Assert.Contains("Keep on a lead.", details);
            Assert.Contains("pet-portrait", details);
            var form = new Dictionary<string, string>
            {
                ["Name"] = "Max", ["Species"] = "Dog", ["Breed"] = "Golden Retriever", ["AgeYears"] = "101",
                ["MedicalNotes"] = "No known allergies.\nMedication with breakfast.",
                ["FeedingInstructions"] = "Two cups at 8 AM.", ["CareInstructions"] = "Short walks only."
            };
            var invalid = await Post(client, $"/Pets/Edit/{petId}", form);
            Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
            Assert.Contains("Age must be between", await invalid.Content.ReadAsStringAsync());
            form["AgeYears"] = "4";
            Assert.Equal(HttpStatusCode.Redirect, (await Post(client, $"/Pets/Edit/{petId}", form)).StatusCode);
            using var verify = factory.Services.CreateScope();
            var saved = await verify.ServiceProvider.GetRequiredService<PetSittingDbContext>().Pets.AsNoTracking().SingleAsync(p => p.Id == petId);
            Assert.Equal(4, saved.AgeYears);
            Assert.Equal(form["MedicalNotes"], saved.MedicalNotes);
            Assert.Equal(form["FeedingInstructions"], saved.FeedingInstructions);
            Assert.Equal(form["CareInstructions"], saved.CareInstructions);
            Assert.Equal(ownerId, saved.OwnerId);
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
            await db.Pets.Where(p => p.OwnerId == ownerId).ExecuteDeleteAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByEmailAsync(email);
            if (owner is not null) Assert.True((await users.DeleteAsync(owner)).Succeeded);
        }
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(path);
        var token = Regex.Match(html, "<input(?=[^>]*name=\"__RequestVerificationToken\")(?=[^>]*value=\"([^\"]+)\")[^>]*>").Groups[1].Value;
        Assert.NotEmpty(token);
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(token);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
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
