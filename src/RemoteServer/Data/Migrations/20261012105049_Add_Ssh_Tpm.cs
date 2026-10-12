using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class Add_Ssh_Tpm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingSshPublicKey",
                table: "Devices",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PendingSshUntil",
                table: "Devices",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SshKeyProvider",
                table: "Devices",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingSshPublicKey",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PendingSshUntil",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "SshKeyProvider",
                table: "Devices");
        }
    }
}
