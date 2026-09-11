using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeOps.Api.Migrations;

public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Devices",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                SourceDeviceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Devices", x => x.Id));

        migrationBuilder.CreateTable(
            name: "MeasurementPoints",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                DeviceId = table.Column<int>(type: "int", nullable: false),
                Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Unit = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MeasurementPoints", x => x.Id);
                table.ForeignKey("FK_MeasurementPoints_Devices_DeviceId", x => x.DeviceId, "Devices", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Measurements",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                MeasurementPointId = table.Column<int>(type: "int", nullable: false),
                Value = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Measurements", x => x.Id);
                table.ForeignKey("FK_Measurements_MeasurementPoints_MeasurementPointId", x => x.MeasurementPointId, "MeasurementPoints", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_Devices_Source_SourceDeviceId", "Devices", new[] { "Source", "SourceDeviceId" }, unique: true);
        migrationBuilder.CreateIndex("IX_MeasurementPoints_DeviceId_Key", "MeasurementPoints", new[] { "DeviceId", "Key" }, unique: true);
        migrationBuilder.CreateIndex("IX_Measurements_MeasurementPointId_Timestamp", "Measurements", new[] { "MeasurementPointId", "Timestamp" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Measurements");
        migrationBuilder.DropTable("MeasurementPoints");
        migrationBuilder.DropTable("Devices");
    }
}
