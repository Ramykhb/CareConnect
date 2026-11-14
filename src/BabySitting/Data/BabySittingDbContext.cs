using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using BabySitting.Models;

namespace BabySitting.Data;

public sealed class BabySittingDbContext(DbContextOptions<BabySittingDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<BabysitterProfile> BabysitterProfiles => Set<BabysitterProfile>();
    public DbSet<ChildcareRequest> ChildcareRequests => Set<ChildcareRequest>();
    public DbSet<BabysittingOffer> BabysittingOffers => Set<BabysittingOffer>();
    public DbSet<BabysittingBooking> BabysittingBookings => Set<BabysittingBooking>();
    public DbSet<SitterService> SitterServices => Set<SitterService>();
    public DbSet<SitterAvailability> SitterAvailabilities => Set<SitterAvailability>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<MessageOutbox> MessageOutboxEntries => Set<MessageOutbox>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<FamilyProfile> FamilyProfiles => Set<FamilyProfile>();
    public DbSet<AccountPhoto> AccountPhotos => Set<AccountPhoto>();
    public DbSet<ParentRequestDraft> ParentRequestDrafts => Set<ParentRequestDraft>();
    public DbSet<SitterScheduleException> SitterScheduleExceptions => Set<SitterScheduleException>();
    public DbSet<BookingCareTask> BookingCareTasks => Set<BookingCareTask>();
    public DbSet<BookingUpdate> BookingUpdates => Set<BookingUpdate>();
    public DbSet<BookingUpdatePhoto> BookingUpdatePhotos => Set<BookingUpdatePhoto>();
    public DbSet<SitterPayout> SitterPayouts => Set<SitterPayout>();
    public DbSet<PayoutAllocation> PayoutAllocations => Set<PayoutAllocation>();
    public DbSet<AlertPreferences> AlertPreferences => Set<AlertPreferences>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // An account has one platform role: customer, sitter, or manager.
        // The unique index also prevents conflicting assignments under concurrent writes.
        modelBuilder.Entity<IdentityUserRole<Guid>>().HasIndex(x => x.UserId)
            .IsUnique().HasDatabaseName("UX_AspNetUserRoles_UserId");
        modelBuilder.Entity<BabysitterProfile>().HasOne(x => x.User).WithOne(x => x.SitterProfile).HasForeignKey<BabysitterProfile>(x => x.UserId);
        modelBuilder.Entity<SitterService>().HasOne(x => x.SitterProfile).WithMany(x => x.Services).HasForeignKey(x => x.SitterProfileId);
        modelBuilder.Entity<SitterAvailability>().HasOne(x => x.SitterProfile).WithMany(x => x.Availability).HasForeignKey(x => x.SitterProfileId);
        modelBuilder.Entity<ChildcareRequest>().HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        modelBuilder.Entity<FamilyProfile>().HasOne(x => x.User).WithOne().HasForeignKey<FamilyProfile>(x => x.UserId);
        modelBuilder.Entity<ChildcareRequest>().HasOne(x => x.FamilyProfile).WithMany().HasForeignKey(x => x.FamilyProfileId);
        modelBuilder.Entity<BabysittingOffer>().HasOne(x => x.Request).WithMany(x => x.Offers).HasForeignKey(x => x.RequestId);
        modelBuilder.Entity<BabysittingOffer>().HasOne(x => x.SitterProfile).WithMany().HasForeignKey(x => x.SitterProfileId);
        modelBuilder.Entity<BabysittingBooking>().HasOne(x => x.Request).WithMany().HasForeignKey(x => x.RequestId);
        modelBuilder.Entity<BabysittingBooking>().HasOne(x => x.Offer).WithMany().HasForeignKey(x => x.OfferId);
        modelBuilder.Entity<BabysittingBooking>().HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        modelBuilder.Entity<BabysittingBooking>().HasOne(x => x.SitterProfile).WithMany().HasForeignKey(x => x.SitterProfileId);
        modelBuilder.Entity<Payment>().HasOne(x => x.Booking).WithMany(x => x.Payments).HasForeignKey(x => x.BookingId);
        modelBuilder.Entity<Payment>().HasOne(x => x.Payer).WithMany().HasForeignKey(x => x.PayerId);
        modelBuilder.Entity<Review>().HasOne(x => x.Booking).WithOne(x => x.Review).HasForeignKey<Review>(x => x.BookingId);
        modelBuilder.Entity<Review>().HasOne(x => x.Reviewer).WithMany().HasForeignKey(x => x.ReviewerId);
        modelBuilder.Entity<Notification>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        modelBuilder.Entity<BabysitterProfile>().HasIndex(x => x.UserId).IsUnique();
        modelBuilder.Entity<SitterService>().HasIndex(x => new { x.SitterProfileId, x.ServiceType }).IsUnique();
        modelBuilder.Entity<SitterAvailability>().HasIndex(x => new { x.SitterProfileId, x.StartsAtUtc, x.EndsAtUtc });
        modelBuilder.Entity<ChildcareRequest>().HasIndex(x => new { x.Status, x.City, x.StartsAtUtc });
        modelBuilder.Entity<ChildcareRequest>().HasIndex(x => x.CorrelationId);
        modelBuilder.Entity<BabysittingOffer>().HasIndex(x => new { x.RequestId, x.SitterProfileId }).IsUnique();
        modelBuilder.Entity<BabysittingBooking>().HasIndex(x => x.OfferId).IsUnique();
        modelBuilder.Entity<BabysittingBooking>().HasIndex(x => new { x.SitterProfileId, x.Status, x.StartsAtUtc, x.EndsAtUtc });
        modelBuilder.Entity<BabysittingBooking>().HasIndex(x => x.CorrelationId);
        modelBuilder.Entity<Payment>().HasIndex(x => x.IdempotencyKey).IsUnique();
        modelBuilder.Entity<Notification>().HasIndex(x => new { x.UserId, x.ReadAtUtc, x.CreatedAtUtc });
        modelBuilder.Entity<MessageOutbox>().HasIndex(x => new { x.PublishedAtUtc, x.NextAttemptAtUtc, x.CreatedAtUtc });
        modelBuilder.Entity<ChildcareRequest>().HasIndex(x => new { x.ExternalPlatform, x.ExternalRequestId }).IsUnique().HasFilter("[ExternalPlatform] IS NOT NULL AND [ExternalRequestId] IS NOT NULL");
        modelBuilder.Entity<BabysittingBooking>().HasIndex(x => x.RequestId).IsUnique().HasFilter("[Status] IN (0, 1, 2, 3)");
        modelBuilder.Entity<Payment>().HasIndex(x => new { x.Provider, x.ProviderTransactionId }).IsUnique().HasFilter("[ProviderTransactionId] IS NOT NULL");
        modelBuilder.Entity<ProcessedMessage>().HasKey(x => new { x.EventId, x.Consumer });
        modelBuilder.Entity<BabysitterProfile>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_BabysitterProfile_Rate", "[HourlyRate] >= 0");
            t.HasCheckConstraint("CK_BabysitterProfile_Experience", "[YearsOfExperience] BETWEEN 0 AND 80");
            t.HasCheckConstraint("CK_BabysitterProfile_Verification", "[VerificationStatus] BETWEEN 0 AND 3");
        });
        modelBuilder.Entity<ChildcareRequest>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_ChildcareRequest_Dates", "[EndsAtUtc] > [StartsAtUtc]");
            t.HasCheckConstraint("CK_ChildcareRequest_Budget", "[Budget] >= 0");
            t.HasCheckConstraint("CK_ChildcareRequest_Status", "[Status] BETWEEN 0 AND 4");
            t.HasCheckConstraint("CK_ChildcareRequest_Owner", "[CustomerId] IS NOT NULL OR [ExternalRequestId] IS NOT NULL");
            t.HasCheckConstraint("CK_ChildcareRequest_ExternalLink", "([ExternalRequestId] IS NULL AND [ExternalPlatform] IS NULL) OR ([ExternalRequestId] IS NOT NULL AND [ExternalPlatform] = 'PetSitting' AND [CorrelationId] IS NOT NULL)");
            t.HasCheckConstraint("CK_ChildcareRequest_Children", "[NumberOfChildren] BETWEEN 1 AND 20");
            t.HasCheckConstraint("CK_ChildcareRequest_Ages", "[YoungestChildAgeYears] >= 0 AND [OldestChildAgeYears] <= 17 AND [YoungestChildAgeYears] <= [OldestChildAgeYears]");
        });
        modelBuilder.Entity<BabysittingOffer>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_BabysittingOffer_Amount", "[Amount] >= 0");
            t.HasCheckConstraint("CK_BabysittingOffer_Status", "[Status] BETWEEN 0 AND 4");
        });
        modelBuilder.Entity<BabysittingBooking>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_BabysittingBooking_Dates", "[EndsAtUtc] > [StartsAtUtc]");
            t.HasCheckConstraint("CK_BabysittingBooking_Amount", "[TotalAmount] >= 0");
            t.HasCheckConstraint("CK_BabysittingBooking_Status", "[Status] BETWEEN 0 AND 6");
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
        modelBuilder.Entity<FamilyProfile>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_FamilyProfile_Children", "[NumberOfChildren] BETWEEN 1 AND 20");
            t.HasCheckConstraint("CK_FamilyProfile_Ages", "[YoungestChildAgeYears] BETWEEN 0 AND 17 AND [OldestChildAgeYears] BETWEEN [YoungestChildAgeYears] AND 17");
        });

        modelBuilder.Entity<ApplicationUser>().Property(u => u.PreferredLanguage).HasDefaultValue("en");
        modelBuilder.Entity<AccountPhoto>().HasIndex(p => p.UserId).IsUnique();
        modelBuilder.Entity<ParentRequestDraft>().HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        modelBuilder.Entity<ParentRequestDraft>().HasOne(x => x.SubmittedRequest).WithMany().HasForeignKey(x => x.SubmittedRequestId);
        modelBuilder.Entity<ParentRequestDraft>().HasIndex(x => new { x.CustomerId, x.UpdatedAtUtc });
        modelBuilder.Entity<ParentRequestDraft>().HasIndex(x => x.SubmittedRequestId).IsUnique().HasFilter("[SubmittedRequestId] IS NOT NULL");

        // Sitter portal: calendar rules, booking care records, alerts, and payout reservations.
        modelBuilder.Entity<SitterScheduleException>().HasOne(x => x.SitterProfile).WithMany(x => x.ScheduleExceptions).HasForeignKey(x => x.SitterProfileId);
        modelBuilder.Entity<SitterScheduleException>().HasIndex(x => new { x.SitterProfileId, x.StartsOn, x.EndsOn });
        modelBuilder.Entity<SitterScheduleException>().ToTable(t => t.HasCheckConstraint("CK_SitterScheduleException_Dates", "[EndsOn] >= [StartsOn] AND ([AllDay] = 1 OR [EndsOn] > [StartsOn] OR [EndTime] > [StartTime])"));
        modelBuilder.Entity<BookingCareTask>().HasOne(x => x.Booking).WithMany(x => x.CareTasks).HasForeignKey(x => x.BookingId);
        modelBuilder.Entity<BookingCareTask>().HasIndex(x => new { x.BookingId, x.Key }).IsUnique();
        modelBuilder.Entity<BookingUpdate>().HasOne(x => x.Booking).WithMany(x => x.Updates).HasForeignKey(x => x.BookingId);
        modelBuilder.Entity<BookingUpdate>().HasIndex(x => new { x.BookingId, x.CreatedAtUtc });
        modelBuilder.Entity<BookingUpdate>().HasIndex(x => new { x.BookingId, x.ClientToken }).IsUnique();
        modelBuilder.Entity<BookingUpdatePhoto>().HasOne(x => x.BookingUpdate).WithOne(x => x.Photo).HasForeignKey<BookingUpdatePhoto>(x => x.BookingUpdateId);
        modelBuilder.Entity<AlertPreferences>().HasOne(p => p.User).WithOne().HasForeignKey<AlertPreferences>(p => p.UserId);
        modelBuilder.Entity<AlertPreferences>().HasIndex(p => p.UserId).IsUnique();
        modelBuilder.Entity<Notification>().HasIndex(n => new { n.UserId, n.SourceKey }).IsUnique().HasFilter("[SourceKey] IS NOT NULL");
        modelBuilder.Entity<Notification>().ToTable(t => t.HasCheckConstraint("CK_Notification_Category", "[Category] BETWEEN 0 AND 4"));
        modelBuilder.Entity<SitterPayout>().HasOne(p => p.SitterProfile).WithMany().HasForeignKey(p => p.SitterProfileId);
        modelBuilder.Entity<SitterPayout>().HasIndex(p => new { p.SitterProfileId, p.ClientToken }).IsUnique();
        modelBuilder.Entity<SitterPayout>().ToTable(t => { t.HasCheckConstraint("CK_SitterPayout_Amount", "[Amount] > 0"); t.HasCheckConstraint("CK_SitterPayout_Status", "[Status] BETWEEN 0 AND 2"); });
        modelBuilder.Entity<PayoutAllocation>().HasOne(a => a.Payout).WithMany(p => p.Allocations).HasForeignKey(a => a.PayoutId);
        modelBuilder.Entity<PayoutAllocation>().HasOne(a => a.Payment).WithMany().HasForeignKey(a => a.PaymentId);
        modelBuilder.Entity<PayoutAllocation>().HasIndex(a => new { a.PayoutId, a.PaymentId }).IsUnique();
        modelBuilder.Entity<PayoutAllocation>().ToTable(t => t.HasCheckConstraint("CK_PayoutAllocation_Amount", "[Amount] > 0"));

        // Avoid SQL Server multiple cascade paths and accidental removal of booking history.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var foreignKey in entityType.GetForeignKeys())
                foreignKey.DeleteBehavior = entityType.ClrType.Namespace == typeof(IdentityUserRole<Guid>).Namespace
                    ? DeleteBehavior.Cascade : DeleteBehavior.NoAction;
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                {
                    property.SetPrecision(18);
                    property.SetScale(2);
                }
            }
        }
        // After the NoAction loop above: a profile photo is removed together with its account.
        modelBuilder.Entity<AccountPhoto>().HasOne(p => p.User).WithOne().HasForeignKey<AccountPhoto>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
