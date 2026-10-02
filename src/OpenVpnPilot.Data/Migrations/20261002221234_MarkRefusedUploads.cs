using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenVpnPilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class MarkRefusedUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UploadRefusedCode",
                table: "Profiles",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UploadRefusedCode",
                table: "Profiles");
        }
    }
}
