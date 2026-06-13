using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecretExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GraphSecretExpiresAt",
                table: "ServerSettings",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SecretExpiryNotifiedAt",
                table: "ServerSettings",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GraphSecretExpiresAt",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "SecretExpiryNotifiedAt",
                table: "ServerSettings");
        }
    }
}
