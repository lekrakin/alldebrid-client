using AdbClient.Data.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AdbClient.Service.Test.Data;

public class CaptureTorrentDownloadPathsMigrationTest
{
    [Fact]
    public async Task Migration_BackfillsExistingTorrentFromConfiguredPaths()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        var migrator = dataContext.GetService<IMigrator>();
        await migrator.MigrateAsync("20250706204358_AddFinishedActionDelay");

        await dataContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Settings" ("SettingId", "Value") VALUES
                ('Storage:DownloadPath', '/storage/original'),
                ('Integrations:ReportedDownloadPath', '/downloads/original');

            INSERT INTO "Torrents" (
                "TorrentId",
                "Hash",
                "DownloadAction",
                "FinishedAction",
                "FinishedActionDelay",
                "HostDownloadAction",
                "DownloadMinSize",
                "DownloadClient",
                "Added",
                "IsFile",
                "RetryCount",
                "DownloadRetryAttempts",
                "TorrentRetryAttempts",
                "DeleteOnError",
                "Lifetime")
            VALUES (
                '11111111-1111-1111-1111-111111111111',
                '0123456789abcdef0123456789abcdef01234567',
                0, 0, 0, 0, 0, 0,
                '2026-09-04 00:00:00+00:00',
                0, 0, 0, 0, 0, 0);
            """);

        await migrator.MigrateAsync();
        dataContext.ChangeTracker.Clear();

        var torrent = await dataContext.Torrents.AsNoTracking().SingleAsync();
        Assert.Equal("/storage/original", torrent.LocalDownloadPath);
        Assert.Equal("/downloads/original", torrent.ClientReportedDownloadPath);
    }
}
