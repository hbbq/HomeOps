using HomeOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeOps.Api.Migrations;

[DbContext(typeof(HomeOpsDbContext))]
[Migration("20261009100000_AddContactHistory")]
public sealed class AddContactHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>("LastOpened", "MeasurementPoints", type: "datetimeoffset", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("LastClosed", "MeasurementPoints", type: "datetimeoffset", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("ContactHistoryCheckedAt", "Devices", type: "datetimeoffset", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("ContactHistoryScanStartedAt", "Devices", type: "datetimeoffset", nullable: true);
        migrationBuilder.AddColumn<string>("ContactHistoryNextPage", "Devices", type: "nvarchar(max)", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("LastOpened", "MeasurementPoints");
        migrationBuilder.DropColumn("LastClosed", "MeasurementPoints");
        migrationBuilder.DropColumn("ContactHistoryCheckedAt", "Devices");
        migrationBuilder.DropColumn("ContactHistoryScanStartedAt", "Devices");
        migrationBuilder.DropColumn("ContactHistoryNextPage", "Devices");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        HomeOpsDbContextModelSnapshot.ConfigureModel(modelBuilder);
}
