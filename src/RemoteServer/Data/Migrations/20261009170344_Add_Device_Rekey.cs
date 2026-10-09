using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class Add_Device_Rekey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CertNotAfter",
                table: "Devices",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyProvider",
                table: "Devices",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PendingCertThumbprint",
                table: "Devices",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PendingCertUntil",
                table: "Devices",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousCertThumbprint",
                table: "Devices",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreviousCertValidUntil",
                table: "Devices",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CertNotAfter",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "KeyProvider",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PendingCertThumbprint",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PendingCertUntil",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PreviousCertThumbprint",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PreviousCertValidUntil",
                table: "Devices");
        }
    }
}
