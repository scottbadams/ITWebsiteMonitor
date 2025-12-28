using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebsiteMonitor.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Step4_InstanceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OutputFolder",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WriteHtmlSnapshot",
                table: "Instances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutputFolder",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "WriteHtmlSnapshot",
                table: "Instances");
        }
    }
}
