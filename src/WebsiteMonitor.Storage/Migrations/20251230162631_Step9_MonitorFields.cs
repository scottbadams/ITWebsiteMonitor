using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebsiteMonitor.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Step9_MonitorFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastDetectedLoginType",
                table: "State",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastFinalUrl",
                table: "State",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUsedIp",
                table: "State",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LoginDetectedEver",
                table: "State",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "LoginDetectedLast",
                table: "State",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRunUtc",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetectedLoginType",
                table: "Checks",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalUrl",
                table: "Checks",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LoginDetected",
                table: "Checks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UsedIp",
                table: "Checks",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastDetectedLoginType",
                table: "State");

            migrationBuilder.DropColumn(
                name: "LastFinalUrl",
                table: "State");

            migrationBuilder.DropColumn(
                name: "LastUsedIp",
                table: "State");

            migrationBuilder.DropColumn(
                name: "LoginDetectedEver",
                table: "State");

            migrationBuilder.DropColumn(
                name: "LoginDetectedLast",
                table: "State");

            migrationBuilder.DropColumn(
                name: "LastRunUtc",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "DetectedLoginType",
                table: "Checks");

            migrationBuilder.DropColumn(
                name: "FinalUrl",
                table: "Checks");

            migrationBuilder.DropColumn(
                name: "LoginDetected",
                table: "Checks");

            migrationBuilder.DropColumn(
                name: "UsedIp",
                table: "Checks");
        }
    }
}
