using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BabySitting.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPeerPaymentResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PeerPaymentAtUtc",
                table: "CombinedCareLinks",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PeerPaymentRevision",
                table: "CombinedCareLinks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "PeerPaymentStatus",
                table: "CombinedCareLinks",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PeerPaymentAtUtc",
                table: "CombinedCareLinks");

            migrationBuilder.DropColumn(
                name: "PeerPaymentRevision",
                table: "CombinedCareLinks");

            migrationBuilder.DropColumn(
                name: "PeerPaymentStatus",
                table: "CombinedCareLinks");
        }
    }
}
