using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PetSitting.Models;

namespace PetSitting.Data;

public sealed class PetSittingDbContext(DbContextOptions<PetSittingDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<PetSitterProfile> PetSitterProfiles => Set<PetSitterProfile>();
    public DbSet<PetCareRequest> PetCareRequests => Set<PetCareRequest>();
    public DbSet<PetCareOffer> PetCareOffers => Set<PetCareOffer>();
    public DbSet<PetBooking> PetBookings => Set<PetBooking>();
    public DbSet<SitterService> SitterServices => Set<SitterService>();
    public DbSet<SitterAvailability> SitterAvailabilities => Set<SitterAvailability>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<MessageOutbox> MessageOutboxEntries => Set<MessageOutbox>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<Pet> Pets => Set<Pet>();
    public DbSet<PetCareRequestPet> PetCareRequestPets => Set<PetCareRequestPet>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<PetSitterProfile>().HasOne(x => x.User).WithOne(x => x.SitterProfile).HasForeignKey<PetSitterProfile>(x => x.UserId);
        modelBuilder.Entity<SitterService>().HasOne(x => x.SitterProfile).WithMany(x => x.Services).HasForeignKey(x => x.SitterProfileId);
        modelBuilder.Entity<SitterAvailability>().HasOne(x => x.SitterProfile).WithMany(x => x.Availability).HasForeignKey(x => x.SitterProfileId);
        modelBuilder.Entity<PetCareRequest>().HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        modelBuilder.Entity<Pet>().HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId);
        modelBuilder.Entity<PetCareRequestPet>().HasKey(x => new { x.PetCareRequestId, x.PetId });
        modelBuilder.Entity<PetCareRequestPet>().HasOne(x => x.PetCareRequest).WithMany(x => x.Pets).HasForeignKey(x => x.PetCareRequestId);
        modelBuilder.Entity<PetCareRequestPet>().HasOne(x => x.Pet).WithMany(x => x.CareRequests).HasForeignKey(x => x.PetId);
        modelBuilder.Entity<PetCareOffer>().HasOne(x => x.Request).WithMany(x => x.Offers).HasForeignKey(x => x.RequestId);
        modelBuilder.Entity<PetCareOffer>().HasOne(x => x.SitterProfile).WithMany().HasForeignKey(x => x.SitterProfileId);
        modelBuilder.Entity<PetBooking>().HasOne(x => x.Request).WithMany().HasForeignKey(x => x.RequestId);
        modelBuilder.Entity<PetBooking>().HasOne(x => x.Offer).WithMany().HasForeignKey(x => x.OfferId);
        modelBuilder.Entity<PetBooking>().HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        modelBuilder.Entity<PetBooking>().HasOne(x => x.SitterProfile).WithMany().HasForeignKey(x => x.SitterProfileId);
        modelBuilder.Entity<Payment>().HasOne(x => x.Booking).WithMany(x => x.Payments).HasForeignKey(x => x.BookingId);
        modelBuilder.Entity<Payment>().HasOne(x => x.Payer).WithMany().HasForeignKey(x => x.PayerId);
        modelBuilder.Entity<Review>().HasOne(x => x.Booking).WithOne(x => x.Review).HasForeignKey<Review>(x => x.BookingId);
        modelBuilder.Entity<Review>().HasOne(x => x.Reviewer).WithMany().HasForeignKey(x => x.ReviewerId);
        modelBuilder.Entity<Notification>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        modelBuilder.Entity<PetSitterProfile>().HasIndex(x => x.UserId).IsUnique();
        modelBuilder.Entity<SitterService>().HasIndex(x => new { x.SitterProfileId, x.ServiceType }).IsUnique();
        modelBuilder.Entity<SitterAvailability>().HasIndex(x => new { x.SitterProfileId, x.StartsAtUtc, x.EndsAtUtc });
        modelBuilder.Entity<PetCareRequest>().HasIndex(x => new { x.Status, x.City, x.StartsAtUtc });
        modelBuilder.Entity<PetCareRequest>().HasIndex(x => x.CorrelationId);
        modelBuilder.Entity<PetCareOffer>().HasIndex(x => new { x.RequestId, x.SitterProfileId }).IsUnique();
        modelBuilder.Entity<PetBooking>().HasIndex(x => x.OfferId).IsUnique();
        modelBuilder.Entity<PetBooking>().HasIndex(x => new { x.SitterProfileId, x.Status, x.StartsAtUtc, x.EndsAtUtc });
        modelBuilder.Entity<PetBooking>().HasIndex(x => x.CorrelationId);
        modelBuilder.Entity<Payment>().HasIndex(x => x.IdempotencyKey).IsUnique();
        modelBuilder.Entity<Notification>().HasIndex(x => new { x.UserId, x.ReadAtUtc, x.CreatedAtUtc });
        modelBuilder.Entity<MessageOutbox>().HasIndex(x => new { x.PublishedAtUtc, x.NextAttemptAtUtc, x.CreatedAtUtc });
        modelBuilder.Entity<PetCareRequest>().HasIndex(x => new { x.ExternalPlatform, x.ExternalRequestId }).IsUnique().HasFilter("[ExternalPlatform] IS NOT NULL AND [ExternalRequestId] IS NOT NULL");
        modelBuilder.Entity<PetBooking>().HasIndex(x => x.RequestId).IsUnique().HasFilter("[Status] IN (0, 1, 2, 3)");
        modelBuilder.Entity<Payment>().HasIndex(x => new { x.Provider, x.ProviderTransactionId }).IsUnique().HasFilter("[ProviderTransactionId] IS NOT NULL");
        modelBuilder.Entity<ProcessedMessage>().HasKey(x => new { x.EventId, x.Consumer });
        modelBuilder.Entity<PetSitterProfile>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_PetSitterProfile_Rate", "[HourlyRate] >= 0");
            t.HasCheckConstraint("CK_PetSitterProfile_Experience", "[YearsOfExperience] BETWEEN 0 AND 80");
            t.HasCheckConstraint("CK_PetSitterProfile_Verification", "[VerificationStatus] BETWEEN 0 AND 3");
        });
        modelBuilder.Entity<PetCareRequest>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_PetCareRequest_Dates", "[EndsAtUtc] > [StartsAtUtc]");
            t.HasCheckConstraint("CK_PetCareRequest_Budget", "[Budget] >= 0");
            t.HasCheckConstraint("CK_PetCareRequest_Status", "[Status] BETWEEN 0 AND 4");
            t.HasCheckConstraint("CK_PetCareRequest_Owner", "[CustomerId] IS NOT NULL OR [ExternalRequestId] IS NOT NULL");
            t.HasCheckConstraint("CK_PetCareRequest_ExternalLink", "([ExternalRequestId] IS NULL AND [ExternalPlatform] IS NULL) OR ([ExternalRequestId] IS NOT NULL AND [ExternalPlatform] = 'BabySitting' AND [CorrelationId] IS NOT NULL)");
        });
        modelBuilder.Entity<PetCareOffer>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_PetCareOffer_Amount", "[Amount] >= 0");
            t.HasCheckConstraint("CK_PetCareOffer_Status", "[Status] BETWEEN 0 AND 4");
        });
        modelBuilder.Entity<PetBooking>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_PetBooking_Dates", "[EndsAtUtc] > [StartsAtUtc]");
            t.HasCheckConstraint("CK_PetBooking_Amount", "[TotalAmount] >= 0");
            t.HasCheckConstraint("CK_PetBooking_Status", "[Status] BETWEEN 0 AND 6");
        });
        modelBuilder.Entity<SitterAvailability>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_SitterAvailability_Dates", "[EndsAtUtc] > [StartsAtUtc]");
        });
        modelBuilder.Entity<Payment>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Payment_Amount", "[Amount] > 0");
            t.HasCheckConstraint("CK_Payment_Status", "[Status] BETWEEN 0 AND 3");
        });
        modelBuilder.Entity<Review>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Review_Rating", "[Rating] BETWEEN 1 AND 5");
        });
        modelBuilder.Entity<MessageOutbox>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_MessageOutbox_Attempts", "[Attempts] >= 0");
            t.HasCheckConstraint("CK_MessageOutbox_Version", "[Version] >= 1");
            t.HasCheckConstraint("CK_MessageOutbox_Payload", "ISJSON([PayloadJson]) = 1");
        });

        // Avoid SQL Server multiple cascade paths and accidental removal of booking history.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var foreignKey in entityType.GetForeignKeys())
                foreignKey.DeleteBehavior = DeleteBehavior.NoAction;
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                {
                    property.SetPrecision(18);
                    property.SetScale(2);
                }
            }
        }
    }
}
