using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PetSitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableNotificationEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmailAttempts",
                table: "Notifications",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EmailLastError",
                table: "Notifications",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EmailLeaseId",
                table: "Notifications",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailLeaseUntilUtc",
                table: "Notifications",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailNextAttemptAtUtc",
                table: "Notifications",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailPending",
                table: "Notifications",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailSentAtUtc",
                table: "Notifications",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_EmailPending_EmailNextAttemptAtUtc",
                table: "Notifications",
                columns: new[] { "EmailPending", "EmailNextAttemptAtUtc" },
                filter: "[EmailPending] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_EmailPending_EmailNextAttemptAtUtc",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "EmailAttempts",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "EmailLastError",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "EmailLeaseId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "EmailLeaseUntilUtc",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "EmailNextAttemptAtUtc",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "EmailPending",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "EmailSentAtUtc",
                table: "Notifications");
        }
    }
}
