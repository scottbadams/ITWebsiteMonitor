using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebsiteMonitor.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Step6_AlertingEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DownFirstNotifiedUtc",
                table: "State",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastNotifiedUtc",
                table: "State",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextNotifyUtc",
                table: "State",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecoveredDueUtc",
                table: "State",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecoveredNotifiedUtc",
                table: "State",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    EventId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<long>(type: "INTEGER", nullable: true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.EventId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropColumn(
                name: "DownFirstNotifiedUtc",
                table: "State");

            migrationBuilder.DropColumn(
                name: "LastNotifiedUtc",
                table: "State");

            migrationBuilder.DropColumn(
                name: "NextNotifyUtc",
                table: "State");

            migrationBuilder.DropColumn(
                name: "RecoveredDueUtc",
                table: "State");

            migrationBuilder.DropColumn(
                name: "RecoveredNotifiedUtc",
                table: "State");
        }
    }
}
