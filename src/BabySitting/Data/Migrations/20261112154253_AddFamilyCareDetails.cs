using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabySitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFamilyCareDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HealthNotes",
                table: "FamilyProfiles",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OldestChildAgeYears",
                table: "FamilyProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Routines",
                table: "FamilyProfiles",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "YoungestChildAgeYears",
                table: "FamilyProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_FamilyProfile_Ages",
                table: "FamilyProfiles",
                sql: "[YoungestChildAgeYears] BETWEEN 0 AND 17 AND [OldestChildAgeYears] BETWEEN [YoungestChildAgeYears] AND 17");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FamilyProfile_Ages",
                table: "FamilyProfiles");

            migrationBuilder.DropColumn(
                name: "HealthNotes",
                table: "FamilyProfiles");

            migrationBuilder.DropColumn(
                name: "OldestChildAgeYears",
                table: "FamilyProfiles");

            migrationBuilder.DropColumn(
                name: "Routines",
                table: "FamilyProfiles");

            migrationBuilder.DropColumn(
                name: "YoungestChildAgeYears",
                table: "FamilyProfiles");
        }
    }
}
