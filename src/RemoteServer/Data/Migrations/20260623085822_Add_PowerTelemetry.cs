using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class Add_PowerTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AcOnline",
                table: "Devices",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "BatteryPercent",
                table: "Devices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SleepAcMinutes",
                table: "Devices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SleepDcMinutes",
                table: "Devices",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcOnline",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "BatteryPercent",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "SleepAcMinutes",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "SleepDcMinutes",
                table: "Devices");
        }
    }
}
