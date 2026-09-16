using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenVpnPilot.Data.Migrations
{
    /// <summary>
    /// Drops the watched directories and the mark that put what they brought in under New.
    /// </summary>
    /// <remarks>
    /// Keeping a store in step with a directory turned out to cause more trouble than it saved, and
    /// importing or opening a package does the same job when somebody asks for it. The profiles a
    /// directory brought in stay; only the watching goes. Their source becomes an ordinary import,
    /// because the value that named a watched directory no longer means anything.
    /// </remarks>
    public partial class RemoveWatchedFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Before the columns change. Dropping one rebuilds the table at the end of the migration,
            // and a statement issued while that rebuild is pending runs against a table in between.
            migrationBuilder.Sql("UPDATE \"Profiles\" SET \"Source\" = 1 WHERE \"Source\" = 2;");

            migrationBuilder.DropTable(
                name: "WatchedFolders");

            migrationBuilder.DropColumn(
                name: "DiscoveredAt",
                table: "Profiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DiscoveredAt",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WatchedFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AutoImport = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRecursive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastScanAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchedFolders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchedFolders_Path",
                table: "WatchedFolders",
                column: "Path",
                unique: true);
        }
    }
}
