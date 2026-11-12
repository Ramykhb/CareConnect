using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using PetSitting.Models;
using Xunit;

namespace PetSitting.Tests;

// Opt in with CARECONNECT_SQL_TESTS=1. All inserted test data is rolled back.
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CARECONNECT_SQL_TESTS") != "1")
            Skip = "Set CARECONNECT_SQL_TESTS=1 to test the local SQL Server databases.";
    }
}

public sealed class DatabaseTests
{
    private static WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

    [SqlServerFact]
    public async Task Database_rejects_a_second_role_for_the_same_account()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var user = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Role", LastName = "Test" };
            db.Users.Add(user);
            var customerRole = await db.Roles.SingleAsync(x => x.Name == Roles.Customer);
            var sitterRole = await db.Roles.SingleAsync(x => x.Name == Roles.Sitter);
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = customerRole.Id });
            await db.SaveChangesAsync();
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = sitterRole.Id });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains(((SqlException)error.InnerException!).Number, new[] { 2601, 2627 });
            Assert.Contains("UX_AspNetUserRoles_UserId", error.InnerException.Message);
        });
    }

    [SqlServerFact]
    public async Task Database_has_all_migrations_and_no_pending_model_changes()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.True(await db.Database.CanConnectAsync());
    }

    [SqlServerFact]
    public async Task Duplicate_processed_event_is_rejected_for_the_same_consumer()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var eventId = Guid.NewGuid();
            db.ProcessedMessages.Add(new ProcessedMessage { EventId = eventId, Consumer = "test", CorrelationId = Guid.NewGuid() });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            db.ProcessedMessages.Add(new ProcessedMessage { EventId = eventId, Consumer = "test", CorrelationId = Guid.NewGuid() });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains(((SqlException)error.InnerException!).Number, new[] { 2601, 2627 });
        });
    }

    [SqlServerFact]
    public async Task Payment_idempotency_key_prevents_duplicate_payment_records()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var booking = CreateBooking();
            db.Add(booking);
            await db.SaveChangesAsync();
            var key = Guid.NewGuid();
            db.Payments.Add(new Payment { BookingId = booking.Id, PayerId = booking.CustomerId, Amount = 20, IdempotencyKey = key });
            await db.SaveChangesAsync();
            db.Payments.Add(new Payment { BookingId = booking.Id, PayerId = booking.CustomerId, Amount = 20, IdempotencyKey = key });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains(((SqlException)error.InnerException!).Number, new[] { 2601, 2627 });
        });
    }

    [SqlServerFact]
    public async Task Database_rejects_reviews_outside_the_rating_range()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var booking = CreateBooking();
            db.Add(booking);
            await db.SaveChangesAsync();
            db.Reviews.Add(new Review { BookingId = booking.Id, ReviewerId = booking.CustomerId, Rating = 6 });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(547, ((SqlException)error.InnerException!).Number);
            Assert.Contains("CK_Review_Rating", error.InnerException.Message);
        });
    }

    [SqlServerFact]
    public async Task Database_rejects_availability_with_reversed_dates()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var profile = CreateBooking().SitterProfile;
            db.Add(profile);
            await db.SaveChangesAsync();
            db.SitterAvailabilities.Add(new SitterAvailability
            {
                SitterProfileId = profile.Id,
                StartsAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                EndsAtUtc = DateTimeOffset.UtcNow
            });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(547, ((SqlException)error.InnerException!).Number);
            Assert.Contains("CK_SitterAvailability_Dates", error.InnerException.Message);
        });
    }

    private static PetBooking CreateBooking()
    {
        var customer = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Test", LastName = "Customer" };
        var sitter = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Test", LastName = "Sitter" };
        var profile = new PetSitterProfile { User = sitter, UserId = sitter.Id, Bio = "Integration test", City = "Beirut" };
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var request = new PetCareRequest
        {
            Customer = customer, CustomerId = customer.Id, Title = "Test request", Description = "Integration test",
            City = "Beirut", StartsAtUtc = start, EndsAtUtc = start.AddHours(2), Budget = 20
        };
        var offer = new PetCareOffer { Request = request, RequestId = request.Id, SitterProfile = profile, SitterProfileId = profile.Id, Amount = 20 };
        return new PetBooking
        {
            Request = request, RequestId = request.Id, Offer = offer, OfferId = offer.Id,
            Customer = customer, CustomerId = customer.Id, SitterProfile = profile, SitterProfileId = profile.Id,
            StartsAtUtc = start, EndsAtUtc = start.AddHours(2), TotalAmount = 20
        };
    }
}
