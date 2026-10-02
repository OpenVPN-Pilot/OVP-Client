using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenVpnPilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class MarkConfigurationEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ConfigurationChanged",
                table: "PendingChanges",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfigurationChanged",
                table: "PendingChanges");
        }
    }
}
