using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PetSitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestAssistantConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequestAssistantConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessagesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 100000, nullable: false),
                    StateJson = table.Column<string>(type: "nvarchar(max)", maxLength: 20000, nullable: false),
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestAssistantConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestAssistantConversations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestAssistantConversations_UpdatedAtUtc",
                table: "RequestAssistantConversations",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RequestAssistantConversations_UserId",
                table: "RequestAssistantConversations",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequestAssistantConversations");
        }
    }
}
