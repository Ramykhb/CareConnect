using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PetSitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSitterBookingCareAndCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleRevision",
                table: "PetSitterProfiles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAtUtc",
                table: "PetBookings",
                type: "datetimeoffset",
                nullable: true);

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
                        name: "FK_BookingCareTasks_PetBookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "PetBookings",
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
                        name: "FK_BookingUpdates_PetBookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "PetBookings",
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
                        name: "FK_SitterScheduleExceptions_PetSitterProfiles_SitterProfileId",
                        column: x => x.SitterProfileId,
                        principalTable: "PetSitterProfiles",
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
                name: "IX_SitterScheduleExceptions_SitterProfileId_StartsOn_EndsOn",
                table: "SitterScheduleExceptions",
                columns: new[] { "SitterProfileId", "StartsOn", "EndsOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingCareTasks");

            migrationBuilder.DropTable(
                name: "BookingUpdatePhotos");

            migrationBuilder.DropTable(
                name: "SitterScheduleExceptions");

            migrationBuilder.DropTable(
                name: "BookingUpdates");

            migrationBuilder.DropColumn(
                name: "ScheduleRevision",
                table: "PetSitterProfiles");

            migrationBuilder.DropColumn(
                name: "StartedAtUtc",
                table: "PetBookings");
        }
    }
}
