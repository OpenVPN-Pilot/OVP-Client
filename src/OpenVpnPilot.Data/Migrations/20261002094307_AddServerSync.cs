using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenVpnPilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServerSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Realm = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Cursor = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessfulPullAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessfulPushAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastRequestId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UserDisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UserRole = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    UserProvider = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingChanges_Kind_EntityId",
                table: "PendingChanges",
                columns: new[] { "Kind", "EntityId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingChanges");

            migrationBuilder.DropTable(
                name: "SyncStates");
        }
    }
}
