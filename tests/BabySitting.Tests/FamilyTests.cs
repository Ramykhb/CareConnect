using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Repositories;
using BabySitting.ViewModels;
using Xunit;

namespace BabySitting.Tests;

public sealed class FamilyTests
{
    private static WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

    private static List<ValidationResult> Validate(FamilyFormViewModel form)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(form, new ValidationContext(form), results, validateAllProperties: true);
        return results;
    }

    [Theory]
    [InlineData("/Family")]
    [InlineData("/Family/Create")]
    public async Task My_family_requires_a_signed_in_parent(string path)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Identity/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public void Family_form_rejects_missing_city_and_out_of_range_values()
    {
        var invalid = Validate(new FamilyFormViewModel { NumberOfChildren = 0, YoungestChildAgeYears = 18, City = "" })
            .SelectMany(r => r.MemberNames).ToHashSet();
        Assert.Contains(nameof(FamilyFormViewModel.NumberOfChildren), invalid);
        Assert.Contains(nameof(FamilyFormViewModel.YoungestChildAgeYears), invalid);
        Assert.Contains(nameof(FamilyFormViewModel.City), invalid);
    }

    [Fact]
    public void Family_form_rejects_an_oldest_age_below_the_youngest()
    {
        var form = new FamilyFormViewModel { NumberOfChildren = 2, YoungestChildAgeYears = 6, OldestChildAgeYears = 3, City = "Beirut" };
        Assert.Contains(form.Validate(new ValidationContext(form)), r => r.MemberNames.Contains(nameof(FamilyFormViewModel.OldestChildAgeYears)));
    }

    [Fact]
    public void Family_form_accepts_a_complete_profile()
    {
        var form = new FamilyFormViewModel { NumberOfChildren = 2, YoungestChildAgeYears = 3, OldestChildAgeYears = 7, City = "Beirut" };
        Assert.Empty(Validate(form));
        Assert.Empty(form.Validate(new ValidationContext(form)));
    }

    // Uses the local SQL Server (CARECONNECT_SQL_TESTS=1); data is rolled back.
    [SqlServerFact]
    public async Task Repository_only_returns_and_deletes_the_users_own_profile()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        var repository = new FamilyRepository(db);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var parent = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Parent", LastName = "One" };
            var other = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Parent", LastName = "Two" };
            db.Users.AddRange(parent, other);
            await db.SaveChangesAsync();

            await repository.AddAsync(new FamilyProfile { UserId = parent.Id, NumberOfChildren = 2, YoungestChildAgeYears = 3, OldestChildAgeYears = 7, City = "Beirut" });

            Assert.NotNull(await repository.GetForUserAsync(parent.Id));
            Assert.Null(await repository.GetForUserAsync(other.Id));
            Assert.False(await repository.DeleteAsync(other.Id));
            Assert.True(await repository.DeleteAsync(parent.Id));
            Assert.Null(await repository.GetForUserAsync(parent.Id));
        });
    }

    [SqlServerFact]
    public async Task Database_rejects_an_oldest_age_below_the_youngest()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BabySittingDbContext>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var parent = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Parent", LastName = "Check" };
            db.Users.Add(parent);
            await db.SaveChangesAsync();
            db.FamilyProfiles.Add(new FamilyProfile { UserId = parent.Id, NumberOfChildren = 2, YoungestChildAgeYears = 8, OldestChildAgeYears = 2, City = "Beirut" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }
}
