using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PetSitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSitterReportsPreferencesAndPayouts : Migration
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

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DecidedAtUtc",
                table: "PetCareOffers",
                type: "datetimeoffset",
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
                        name: "FK_SitterPayouts_PetSitterProfiles_SitterProfileId",
                        column: x => x.SitterProfileId,
                        principalTable: "PetSitterProfiles",
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertPreferences");

            migrationBuilder.DropTable(
                name: "PayoutAllocations");

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
                name: "DecidedAtUtc",
                table: "PetCareOffers");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "Notifications");
        }
    }
}
