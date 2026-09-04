using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdbClient.Data.Migrations
{
    /// <inheritdoc />
    public partial class CaptureTorrentDownloadPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientReportedDownloadPath",
                table: "Torrents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocalDownloadPath",
                table: "Torrents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "Torrents"
                SET "LocalDownloadPath" = COALESCE(
                    NULLIF(TRIM((SELECT "Value" FROM "Settings" WHERE "SettingId" = 'Storage:DownloadPath')), ''),
                    NULLIF(TRIM((SELECT "Value" FROM "Settings" WHERE "SettingId" = 'Paths:DownloadPath')), ''),
                    NULLIF(TRIM((SELECT "Value" FROM "Settings" WHERE "SettingId" = 'DownloadClient:DownloadPath')), ''),
                    NULLIF(TRIM((SELECT "Value" FROM "Settings" WHERE "SettingId" = 'DownloadPath')), '')
                );
                """);

            migrationBuilder.Sql(
                """
                UPDATE "Torrents"
                SET "ClientReportedDownloadPath" = COALESCE(
                    NULLIF(TRIM((SELECT "Value" FROM "Settings" WHERE "SettingId" = 'Integrations:ReportedDownloadPath')), ''),
                    NULLIF(TRIM((SELECT "Value" FROM "Settings" WHERE "SettingId" = 'Paths:MappedPath')), ''),
                    NULLIF(TRIM((SELECT "Value" FROM "Settings" WHERE "SettingId" = 'DownloadClient:MappedPath')), ''),
                    NULLIF(TRIM((SELECT "Value" FROM "Settings" WHERE "SettingId" = 'MappedPath')), ''),
                    "LocalDownloadPath"
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientReportedDownloadPath",
                table: "Torrents");

            migrationBuilder.DropColumn(
                name: "LocalDownloadPath",
                table: "Torrents");
        }
    }
}
