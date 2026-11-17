using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PetSitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCombinedCareLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CombinedCareLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocalRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PeerRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OriginPlatform = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    InvitationHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InvitationExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProtectedInvitationCode = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    LocalRevision = table.Column<long>(type: "bigint", nullable: false),
                    PeerRevision = table.Column<long>(type: "bigint", nullable: false),
                    PeerRequestStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PeerBookingStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LastPeerUpdateAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationIssue = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CombinedCareLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CombinedCareLinks_PetCareRequests_LocalRequestId",
                        column: x => x.LocalRequestId,
                        principalTable: "PetCareRequests",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CombinedCareLinks_InvitationHash",
                table: "CombinedCareLinks",
                column: "InvitationHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CombinedCareLinks_LocalRequestId",
                table: "CombinedCareLinks",
                column: "LocalRequestId",
                unique: true,
                filter: "[LocalRequestId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CombinedCareLinks");
        }
    }
}
