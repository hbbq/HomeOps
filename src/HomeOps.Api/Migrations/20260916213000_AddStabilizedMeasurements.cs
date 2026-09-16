using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeOps.Api.Migrations;

public partial class AddStabilizedMeasurements : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ExposedMeasurements",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                MeasurementPointId = table.Column<int>(type: "int", nullable: false),
                Value = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ExposedMeasurements", x => x.Id);
                table.ForeignKey(
                    name: "FK_ExposedMeasurements_MeasurementPoints_MeasurementPointId",
                    column: x => x.MeasurementPointId,
                    principalTable: "MeasurementPoints",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MotionHolds",
            columns: table => new
            {
                MeasurementPointId = table.Column<int>(type: "int", nullable: false),
                DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MotionHolds", x => x.MeasurementPointId);
                table.ForeignKey(
                    name: "FK_MotionHolds_MeasurementPoints_MeasurementPointId",
                    column: x => x.MeasurementPointId,
                    principalTable: "MeasurementPoints",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ExposedMeasurements_MeasurementPointId_Id",
            table: "ExposedMeasurements",
            columns: new[] { "MeasurementPointId", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_MotionHolds_DueAt",
            table: "MotionHolds",
            column: "DueAt");

        migrationBuilder.Sql("""
            WITH Latest AS (
                SELECT MeasurementPointId, Value, Timestamp,
                    ROW_NUMBER() OVER (PARTITION BY MeasurementPointId ORDER BY Timestamp DESC, Id DESC) AS RowNumber
                FROM Measurements
            )
            INSERT INTO ExposedMeasurements (MeasurementPointId, Value, Timestamp)
            SELECT MeasurementPointId, Value, Timestamp
            FROM Latest
            WHERE RowNumber = 1;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "MotionHolds");
        migrationBuilder.DropTable(name: "ExposedMeasurements");
    }
}
