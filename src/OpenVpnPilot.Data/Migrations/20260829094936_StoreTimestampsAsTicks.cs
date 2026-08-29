using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenVpnPilot.Data.Migrations
{
    /// <summary>
    /// Stores every timestamp as ticks rather than as text.
    /// </summary>
    /// <remarks>
    /// SQLite refuses to order or compare a value whose type is DateTimeOffset, so the history could
    /// not be sorted by time or filtered by period at all while the default text storage was used.
    ///
    /// The values already in the database are converted in place before the columns change type.
    /// That conversion goes through the SQLite date functions, which resolve to milliseconds, so
    /// rows written before this migration lose sub-millisecond precision. Nothing in the application
    /// reads these below the second, and values written afterwards are exact.
    /// </remarks>
    public partial class StoreTimestampsAsTicks : Migration
    {
        /// <summary>
        /// Seconds between the epoch of DateTimeOffset and the Unix epoch.
        /// </summary>
        private const string EpochOffsetSeconds = "62135596800";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConvertToTicks(migrationBuilder, "Profiles", "CreatedAt");
            ConvertToTicks(migrationBuilder, "Profiles", "UpdatedAt");
            ConvertToTicks(migrationBuilder, "Profiles", "LastConnectedAt");
            ConvertToTicks(migrationBuilder, "Sessions", "StartedAt");
            ConvertToTicks(migrationBuilder, "Sessions", "EndedAt");
            ConvertToTicks(migrationBuilder, "SessionEvents", "Timestamp");
            ConvertToTicks(migrationBuilder, "WatchedFolders", "LastScanAt");

            migrationBuilder.AlterColumn<long>(
                name: "LastScanAt",
                table: "WatchedFolders",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "StartedAt",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<long>(
                name: "EndedAt",
                table: "Sessions",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Timestamp",
                table: "SessionEvents",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<long>(
                name: "UpdatedAt",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<long>(
                name: "LastConnectedAt",
                table: "Profiles",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "CreatedAt",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "LastScanAt",
                table: "WatchedFolders",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "Sessions",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "EndedAt",
                table: "Sessions",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "Timestamp",
                table: "SessionEvents",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "LastConnectedAt",
                table: "Profiles",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            ConvertToText(migrationBuilder, "Profiles", "CreatedAt");
            ConvertToText(migrationBuilder, "Profiles", "UpdatedAt");
            ConvertToText(migrationBuilder, "Profiles", "LastConnectedAt");
            ConvertToText(migrationBuilder, "Sessions", "StartedAt");
            ConvertToText(migrationBuilder, "Sessions", "EndedAt");
            ConvertToText(migrationBuilder, "SessionEvents", "Timestamp");
            ConvertToText(migrationBuilder, "WatchedFolders", "LastScanAt");
        }

        /// <summary>
        /// Rewrites a text timestamp as ticks.
        /// </summary>
        /// <remarks>
        /// Only rows that still hold text are touched, so running this twice cannot square a value
        /// that has already been converted. The whole seconds and the fraction are computed
        /// separately: multiplying the fraction by ten million keeps it well inside the range a
        /// double represents exactly, while the seconds stay integral throughout.
        /// </remarks>
        private static void ConvertToTicks(MigrationBuilder migrationBuilder, string table, string column)
        {
            migrationBuilder.Sql($"""
                UPDATE "{table}"
                SET "{column}" =
                    (CAST(strftime('%s', "{column}") AS INTEGER) + {EpochOffsetSeconds}) * 10000000
                    + CAST(ROUND((CAST(strftime('%f', "{column}") AS REAL)
                        - CAST(strftime('%S', "{column}") AS INTEGER)) * 10000000) AS INTEGER)
                WHERE "{column}" IS NOT NULL AND typeof("{column}") = 'text';
                """);
        }

        private static void ConvertToText(MigrationBuilder migrationBuilder, string table, string column)
        {
            migrationBuilder.Sql($"""
                UPDATE "{table}"
                SET "{column}" = strftime('%Y-%m-%d %H:%M:%f',
                    "{column}" / 10000000.0 - {EpochOffsetSeconds}, 'unixepoch') || '+00:00'
                WHERE "{column}" IS NOT NULL AND typeof("{column}") = 'integer';
                """);
        }
    }
}
