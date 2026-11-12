using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabySitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    City = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageOutboxEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    RoutingKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageOutboxEntries", x => x.Id);
                    table.CheckConstraint("CK_MessageOutbox_Attempts", "[Attempts] >= 0");
                    table.CheckConstraint("CK_MessageOutbox_Payload", "ISJSON([PayloadJson]) = 1");
                    table.CheckConstraint("CK_MessageOutbox_Version", "[Version] >= 1");
                });

            migrationBuilder.CreateTable(
                name: "ProcessedMessages",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Consumer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedMessages", x => new { x.EventId, x.Consumer });
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "BabysitterProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Bio = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    City = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    YearsOfExperience = table.Column<int>(type: "int", nullable: false),
                    HourlyRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    VerificationStatus = table.Column<int>(type: "int", nullable: false),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsAvailable = table.Column<bool>(type: "bit", nullable: false),
                    Qualifications = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    HasFirstAidTraining = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BabysitterProfiles", x => x.Id);
                    table.CheckConstraint("CK_BabysitterProfile_Experience", "[YearsOfExperience] BETWEEN 0 AND 80");
                    table.CheckConstraint("CK_BabysitterProfile_Rate", "[HourlyRate] >= 0");
                    table.CheckConstraint("CK_BabysitterProfile_Verification", "[VerificationStatus] BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_BabysitterProfiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "FamilyProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NumberOfChildren = table.Column<int>(type: "int", nullable: false),
                    City = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CarePreferences = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamilyProfiles", x => x.Id);
                    table.CheckConstraint("CK_FamilyProfile_Children", "[NumberOfChildren] BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "FK_FamilyProfiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RelativeUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReadAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SitterAvailabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SitterProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SitterAvailabilities", x => x.Id);
                    table.CheckConstraint("CK_SitterAvailability_Dates", "[EndsAtUtc] > [StartsAtUtc]");
                    table.ForeignKey(
                        name: "FK_SitterAvailabilities_BabysitterProfiles_SitterProfileId",
                        column: x => x.SitterProfileId,
                        principalTable: "BabysitterProfiles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SitterServices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SitterProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceType = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SitterServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SitterServices_BabysitterProfiles_SitterProfileId",
                        column: x => x.SitterProfileId,
                        principalTable: "BabysitterProfiles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ChildcareRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ServiceType = table.Column<int>(type: "int", nullable: false),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    City = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ServiceAddress = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Budget = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalPlatform = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    FamilyProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NumberOfChildren = table.Column<int>(type: "int", nullable: false),
                    YoungestChildAgeYears = table.Column<int>(type: "int", nullable: false),
                    OldestChildAgeYears = table.Column<int>(type: "int", nullable: false),
                    CareInstructions = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChildcareRequests", x => x.Id);
                    table.CheckConstraint("CK_ChildcareRequest_Ages", "[YoungestChildAgeYears] >= 0 AND [OldestChildAgeYears] <= 17 AND [YoungestChildAgeYears] <= [OldestChildAgeYears]");
                    table.CheckConstraint("CK_ChildcareRequest_Budget", "[Budget] >= 0");
                    table.CheckConstraint("CK_ChildcareRequest_Children", "[NumberOfChildren] BETWEEN 1 AND 20");
                    table.CheckConstraint("CK_ChildcareRequest_Dates", "[EndsAtUtc] > [StartsAtUtc]");
                    table.CheckConstraint("CK_ChildcareRequest_ExternalLink", "([ExternalRequestId] IS NULL AND [ExternalPlatform] IS NULL) OR ([ExternalRequestId] IS NOT NULL AND [ExternalPlatform] = 'PetSitting' AND [CorrelationId] IS NOT NULL)");
                    table.CheckConstraint("CK_ChildcareRequest_Owner", "[CustomerId] IS NOT NULL OR [ExternalRequestId] IS NOT NULL");
                    table.CheckConstraint("CK_ChildcareRequest_Status", "[Status] BETWEEN 0 AND 4");
                    table.ForeignKey(
                        name: "FK_ChildcareRequests_AspNetUsers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ChildcareRequests_FamilyProfiles_FamilyProfileId",
                        column: x => x.FamilyProfileId,
                        principalTable: "FamilyProfiles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "BabysittingOffers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SitterProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BabysittingOffers", x => x.Id);
                    table.CheckConstraint("CK_BabysittingOffer_Amount", "[Amount] >= 0");
                    table.CheckConstraint("CK_BabysittingOffer_Status", "[Status] BETWEEN 0 AND 4");
                    table.ForeignKey(
                        name: "FK_BabysittingOffers_BabysitterProfiles_SitterProfileId",
                        column: x => x.SitterProfileId,
                        principalTable: "BabysitterProfiles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_BabysittingOffers_ChildcareRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "ChildcareRequests",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "BabysittingBookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OfferId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SitterProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalBookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BabysittingBookings", x => x.Id);
                    table.CheckConstraint("CK_BabysittingBooking_Amount", "[TotalAmount] >= 0");
                    table.CheckConstraint("CK_BabysittingBooking_Dates", "[EndsAtUtc] > [StartsAtUtc]");
                    table.CheckConstraint("CK_BabysittingBooking_Status", "[Status] BETWEEN 0 AND 6");
                    table.ForeignKey(
                        name: "FK_BabysittingBookings_AspNetUsers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_BabysittingBookings_BabysitterProfiles_SitterProfileId",
                        column: x => x.SitterProfileId,
                        principalTable: "BabysitterProfiles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_BabysittingBookings_BabysittingOffers_OfferId",
                        column: x => x.OfferId,
                        principalTable: "BabysittingOffers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_BabysittingBookings_ChildcareRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "ChildcareRequests",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ProviderTransactionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaidAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.CheckConstraint("CK_Payment_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_Payment_Status", "[Status] BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_Payments_AspNetUsers_PayerId",
                        column: x => x.PayerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Payments_BabysittingBookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "BabysittingBookings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reviews", x => x.Id);
                    table.CheckConstraint("CK_Review_Rating", "[Rating] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_Reviews_AspNetUsers_ReviewerId",
                        column: x => x.ReviewerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Reviews_BabysittingBookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "BabysittingBookings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BabysitterProfiles_UserId",
                table: "BabysitterProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BabysittingBookings_CorrelationId",
                table: "BabysittingBookings",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_BabysittingBookings_CustomerId",
                table: "BabysittingBookings",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_BabysittingBookings_OfferId",
                table: "BabysittingBookings",
                column: "OfferId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BabysittingBookings_RequestId",
                table: "BabysittingBookings",
                column: "RequestId",
                unique: true,
                filter: "[Status] IN (0, 1, 2, 3)");

            migrationBuilder.CreateIndex(
                name: "IX_BabysittingBookings_SitterProfileId_Status_StartsAtUtc_EndsAtUtc",
                table: "BabysittingBookings",
                columns: new[] { "SitterProfileId", "Status", "StartsAtUtc", "EndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BabysittingOffers_RequestId_SitterProfileId",
                table: "BabysittingOffers",
                columns: new[] { "RequestId", "SitterProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BabysittingOffers_SitterProfileId",
                table: "BabysittingOffers",
                column: "SitterProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ChildcareRequests_CorrelationId",
                table: "ChildcareRequests",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ChildcareRequests_CustomerId",
                table: "ChildcareRequests",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ChildcareRequests_ExternalPlatform_ExternalRequestId",
                table: "ChildcareRequests",
                columns: new[] { "ExternalPlatform", "ExternalRequestId" },
                unique: true,
                filter: "[ExternalPlatform] IS NOT NULL AND [ExternalRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChildcareRequests_FamilyProfileId",
                table: "ChildcareRequests",
                column: "FamilyProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ChildcareRequests_Status_City_StartsAtUtc",
                table: "ChildcareRequests",
                columns: new[] { "Status", "City", "StartsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FamilyProfiles_UserId",
                table: "FamilyProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageOutboxEntries_PublishedAtUtc_NextAttemptAtUtc_CreatedAtUtc",
                table: "MessageOutboxEntries",
                columns: new[] { "PublishedAtUtc", "NextAttemptAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_ReadAtUtc_CreatedAtUtc",
                table: "Notifications",
                columns: new[] { "UserId", "ReadAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_BookingId",
                table: "Payments",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_IdempotencyKey",
                table: "Payments",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PayerId",
                table: "Payments",
                column: "PayerId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Provider_ProviderTransactionId",
                table: "Payments",
                columns: new[] { "Provider", "ProviderTransactionId" },
                unique: true,
                filter: "[ProviderTransactionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_BookingId",
                table: "Reviews",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_ReviewerId",
                table: "Reviews",
                column: "ReviewerId");

            migrationBuilder.CreateIndex(
                name: "IX_SitterAvailabilities_SitterProfileId_StartsAtUtc_EndsAtUtc",
                table: "SitterAvailabilities",
                columns: new[] { "SitterProfileId", "StartsAtUtc", "EndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SitterServices_SitterProfileId_ServiceType",
                table: "SitterServices",
                columns: new[] { "SitterProfileId", "ServiceType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "MessageOutboxEntries");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "ProcessedMessages");

            migrationBuilder.DropTable(
                name: "Reviews");

            migrationBuilder.DropTable(
                name: "SitterAvailabilities");

            migrationBuilder.DropTable(
                name: "SitterServices");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "BabysittingBookings");

            migrationBuilder.DropTable(
                name: "BabysittingOffers");

            migrationBuilder.DropTable(
                name: "BabysitterProfiles");

            migrationBuilder.DropTable(
                name: "ChildcareRequests");

            migrationBuilder.DropTable(
                name: "FamilyProfiles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");
        }
    }
}
