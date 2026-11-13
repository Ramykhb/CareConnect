using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabySitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBabysitterPortal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RepliedAtUtc",
                table: "Reviews",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SitterReply",
                table: "Reviews",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "Notifications",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "Notifications",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DecidedAtUtc",
                table: "BabysittingOffers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAtUtc",
                table: "BabysittingBookings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleRevision",
                table: "BabysitterProfiles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "AlertPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Requests = table.Column<bool>(type: "bit", nullable: false),
                    Bookings = table.Column<bool>(type: "bit", nullable: false),
                    Payments = table.Column<bool>(type: "bit", nullable: false),
                    Reviews = table.Column<bool>(type: "bit", nullable: false),
                    RequestSound = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "BookingCareTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingCareTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingCareTasks_BabysittingBookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "BabysittingBookings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "BookingUpdates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ClientToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsIssue = table.Column<bool>(type: "bit", nullable: false),
                    HasPhoto = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingUpdates_BabysittingBookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "BabysittingBookings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SitterPayouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SitterProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PaidAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SitterPayouts", x => x.Id);
                    table.CheckConstraint("CK_SitterPayout_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_SitterPayout_Status", "[Status] BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "FK_SitterPayouts_BabysitterProfiles_SitterProfileId",
                        column: x => x.SitterProfileId,
                        principalTable: "BabysitterProfiles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SitterScheduleExceptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SitterProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    AllDay = table.Column<bool>(type: "bit", nullable: false),
                    IsAvailable = table.Column<bool>(type: "bit", nullable: false),
                    RepeatYearly = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SitterScheduleExceptions", x => x.Id);
                    table.CheckConstraint("CK_SitterScheduleException_Dates", "[EndsOn] >= [StartsOn] AND ([AllDay] = 1 OR [EndsOn] > [StartsOn] OR [EndTime] > [StartTime])");
                    table.ForeignKey(
                        name: "FK_SitterScheduleExceptions_BabysitterProfiles_SitterProfileId",
                        column: x => x.SitterProfileId,
                        principalTable: "BabysitterProfiles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "BookingUpdatePhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingUpdateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Bytes = table.Column<byte[]>(type: "varbinary(max)", maxLength: 5242880, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingUpdatePhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingUpdatePhotos_BookingUpdates_BookingUpdateId",
                        column: x => x.BookingUpdateId,
                        principalTable: "BookingUpdates",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PayoutAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayoutId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayoutAllocations", x => x.Id);
                    table.CheckConstraint("CK_PayoutAllocation_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_PayoutAllocations_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PayoutAllocations_SitterPayouts_PayoutId",
                        column: x => x.PayoutId,
                        principalTable: "SitterPayouts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_SourceKey",
                table: "Notifications",
                columns: new[] { "UserId", "SourceKey" },
                unique: true,
                filter: "[SourceKey] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notification_Category",
                table: "Notifications",
                sql: "[Category] BETWEEN 0 AND 4");

            migrationBuilder.CreateIndex(
                name: "IX_AlertPreferences_UserId",
                table: "AlertPreferences",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingCareTasks_BookingId_Key",
                table: "BookingCareTasks",
                columns: new[] { "BookingId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingUpdatePhotos_BookingUpdateId",
                table: "BookingUpdatePhotos",
                column: "BookingUpdateId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingUpdates_BookingId_ClientToken",
                table: "BookingUpdates",
                columns: new[] { "BookingId", "ClientToken" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingUpdates_BookingId_CreatedAtUtc",
                table: "BookingUpdates",
                columns: new[] { "BookingId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PayoutAllocations_PaymentId",
                table: "PayoutAllocations",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_PayoutAllocations_PayoutId_PaymentId",
                table: "PayoutAllocations",
                columns: new[] { "PayoutId", "PaymentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SitterPayouts_SitterProfileId_ClientToken",
                table: "SitterPayouts",
                columns: new[] { "SitterProfileId", "ClientToken" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SitterScheduleExceptions_SitterProfileId_StartsOn_EndsOn",
                table: "SitterScheduleExceptions",
                columns: new[] { "SitterProfileId", "StartsOn", "EndsOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertPreferences");

            migrationBuilder.DropTable(
                name: "BookingCareTasks");

            migrationBuilder.DropTable(
                name: "BookingUpdatePhotos");

            migrationBuilder.DropTable(
                name: "PayoutAllocations");

            migrationBuilder.DropTable(
                name: "SitterScheduleExceptions");

            migrationBuilder.DropTable(
                name: "BookingUpdates");

            migrationBuilder.DropTable(
                name: "SitterPayouts");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_UserId_SourceKey",
                table: "Notifications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Notification_Category",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "RepliedAtUtc",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "SitterReply",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "DecidedAtUtc",
                table: "BabysittingOffers");

            migrationBuilder.DropColumn(
                name: "StartedAtUtc",
                table: "BabysittingBookings");

            migrationBuilder.DropColumn(
                name: "ScheduleRevision",
                table: "BabysitterProfiles");
        }
    }
}
