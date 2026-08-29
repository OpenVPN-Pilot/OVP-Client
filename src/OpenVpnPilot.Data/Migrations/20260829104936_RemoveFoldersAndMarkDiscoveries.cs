using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenVpnPilot.Data.Migrations
{
    /// <summary>
    /// Drops the folder tree and marks profiles a watched directory brought in.
    /// </summary>
    /// <remarks>
    /// Folders were a second way to organise a set that tags and search already cover, and a tree
    /// the user has to maintain is worse than a filter that maintains itself. Any folder that
    /// existed is removed with this migration; the profiles that were filed under one are not
    /// touched, only their filing is.
    ///
    /// The discovery timestamp takes over the one job filing did automatically: telling the user
    /// which profiles arrived without them asking.
    /// </remarks>
    public partial class RemoveFoldersAndMarkDiscoveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Profiles_Folders_FolderId",
                table: "Profiles");

            migrationBuilder.DropTable(
                name: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Profiles_FolderId",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "TargetFolderId",
                table: "WatchedFolders");

            migrationBuilder.DropColumn(
                name: "FolderId",
                table: "Profiles");

            migrationBuilder.AddColumn<long>(
                name: "DiscoveredAt",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscoveredAt",
                table: "Profiles");

            migrationBuilder.AddColumn<Guid>(
                name: "TargetFolderId",
                table: "WatchedFolders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FolderId",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Folders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Icon = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Folders_Folders_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_FolderId",
                table: "Profiles",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_ParentId",
                table: "Folders",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_Profiles_Folders_FolderId",
                table: "Profiles",
                column: "FolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
