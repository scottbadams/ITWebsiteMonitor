using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebsiteMonitor.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddNetworkAccessAndPublicBaseUrlToSystemSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowNetworkAccess",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PublicBaseUrl",
                table: "SystemSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowNetworkAccess",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "PublicBaseUrl",
                table: "SystemSettings");
        }
    }
}
