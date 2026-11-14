using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabySitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddParentRequestDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ParentRequestDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataJson = table.Column<string>(type: "nvarchar(max)", maxLength: 20000, nullable: false),
                    Step = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SubmittedRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParentRequestDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParentRequestDrafts_AspNetUsers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ParentRequestDrafts_ChildcareRequests_SubmittedRequestId",
                        column: x => x.SubmittedRequestId,
                        principalTable: "ChildcareRequests",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ParentRequestDrafts_CustomerId_UpdatedAtUtc",
                table: "ParentRequestDrafts",
                columns: new[] { "CustomerId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ParentRequestDrafts_SubmittedRequestId",
                table: "ParentRequestDrafts",
                column: "SubmittedRequestId",
                unique: true,
                filter: "[SubmittedRequestId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ParentRequestDrafts");
        }
    }
}
