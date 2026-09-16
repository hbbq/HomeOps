using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeOps.Api.Migrations;

public partial class AddSmartThingsOAuth : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SmartThingsAuthorizations",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                ProtectedAccessToken = table.Column<string>(type: "nvarchar(max)", nullable: false),
                ProtectedRefreshToken = table.Column<string>(type: "nvarchar(max)", nullable: false),
                AccessTokenExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                InstalledAppId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                Scope = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                RequiresReauthorization = table.Column<bool>(type: "bit", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_SmartThingsAuthorizations", x => x.Id));

        migrationBuilder.CreateTable(
            name: "SmartThingsAuthorizationStates",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                StateHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_SmartThingsAuthorizationStates", x => x.Id));

        migrationBuilder.CreateIndex("IX_SmartThingsAuthorizationStates_ExpiresAt", "SmartThingsAuthorizationStates", "ExpiresAt");
        migrationBuilder.CreateIndex("IX_SmartThingsAuthorizationStates_StateHash", "SmartThingsAuthorizationStates", "StateHash", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("SmartThingsAuthorizations");
        migrationBuilder.DropTable("SmartThingsAuthorizationStates");
    }
}
