using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebsiteMonitor.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Step5_TargetsChecksState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Checks",
                columns: table => new
                {
                    CheckId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TargetId = table.Column<long>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TcpOk = table.Column<bool>(type: "INTEGER", nullable: false),
                    HttpOk = table.Column<bool>(type: "INTEGER", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    TcpLatencyMs = table.Column<int>(type: "INTEGER", nullable: true),
                    HttpLatencyMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Checks", x => x.CheckId);
                });

            migrationBuilder.CreateTable(
                name: "State",
                columns: table => new
                {
                    TargetId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IsUp = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastCheckUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StateSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastChangeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSummary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_State", x => x.TargetId);
                });

            migrationBuilder.CreateTable(
                name: "Targets",
                columns: table => new
                {
                    TargetId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LoginRule = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    HttpExpectedStatusMin = table.Column<int>(type: "INTEGER", nullable: true),
                    HttpExpectedStatusMax = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Targets", x => x.TargetId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Checks_TargetId_TimestampUtc",
                table: "Checks",
                columns: new[] { "TargetId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Targets_InstanceId_Url",
                table: "Targets",
                columns: new[] { "InstanceId", "Url" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Checks");

            migrationBuilder.DropTable(
                name: "State");

            migrationBuilder.DropTable(
                name: "Targets");
        }
    }
}
