using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class Add_Device_Tpm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TpmAttestation",
                table: "Devices",
                type: "tinyint(1)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TpmManufacturer",
                table: "Devices",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "TpmPresent",
                table: "Devices",
                type: "tinyint(1)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TpmReady",
                table: "Devices",
                type: "tinyint(1)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TpmVersion",
                table: "Devices",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "TpmVulnerableFirmware",
                table: "Devices",
                type: "tinyint(1)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TpmAttestation",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "TpmManufacturer",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "TpmPresent",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "TpmReady",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "TpmVersion",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "TpmVulnerableFirmware",
                table: "Devices");
        }
    }
}
