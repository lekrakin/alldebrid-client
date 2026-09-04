using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdbClient.Data.Migrations
{
    /// <inheritdoc />
    public partial class QbittorrentHidden : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "QbittorrentHidden",
                table: "Torrents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QbittorrentHidden",
                table: "Torrents");
        }
    }
}
