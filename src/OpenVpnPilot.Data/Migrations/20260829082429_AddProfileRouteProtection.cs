using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenVpnPilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileRouteProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ProtectRoutes",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtectRoutes",
                table: "Profiles");
        }
    }
}
