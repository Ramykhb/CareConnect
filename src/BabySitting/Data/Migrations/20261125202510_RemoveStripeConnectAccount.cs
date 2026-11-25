using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabySitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveStripeConnectAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StripeAccountId",
                table: "BabysitterProfiles");

            migrationBuilder.DropColumn(
                name: "StripePayoutsEnabled",
                table: "BabysitterProfiles");

            migrationBuilder.DropColumn(
                name: "StripeTransfersActive",
                table: "BabysitterProfiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StripeAccountId",
                table: "BabysitterProfiles",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StripePayoutsEnabled",
                table: "BabysitterProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "StripeTransfersActive",
                table: "BabysitterProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
