using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabySitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationEmailAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EmailBookings",
                table: "AlertPreferences",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EmailPayments",
                table: "AlertPreferences",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EmailRequests",
                table: "AlertPreferences",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EmailReviews",
                table: "AlertPreferences",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailBookings",
                table: "AlertPreferences");

            migrationBuilder.DropColumn(
                name: "EmailPayments",
                table: "AlertPreferences");

            migrationBuilder.DropColumn(
                name: "EmailRequests",
                table: "AlertPreferences");

            migrationBuilder.DropColumn(
                name: "EmailReviews",
                table: "AlertPreferences");
        }
    }
}
