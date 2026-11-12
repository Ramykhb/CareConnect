using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PetSitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPetCareDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FeedingInstructions",
                table: "Pets",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MedicalNotes",
                table: "Pets",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeedingInstructions",
                table: "Pets");

            migrationBuilder.DropColumn(
                name: "MedicalNotes",
                table: "Pets");
        }
    }
}
