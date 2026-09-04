using AdbClient.Data.Data;
using AdbClient.Data.Enums;
using AdbClient.Data.Models.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AdbClient.Service.Test.Data;

[Collection(SettingsIsolationCollection.Name)]
public class TorrentDataTest
{
    [Fact]
    public async Task Add_CapturesCurrentDownloadPathsForNewTorrent()
    {
        const string localPath = "/storage/current";
        const string reportedPath = "/downloads/current";

        var stored = await AddTorrent(localPath, reportedPath, new Torrent());

        Assert.Equal(localPath, stored.LocalDownloadPath);
        Assert.Equal(reportedPath, stored.ClientReportedDownloadPath);
    }

    [Fact]
    public async Task Add_PreservesPathsAlreadyCapturedByRetriedTorrent()
    {
        const string originalLocalPath = "/storage/original";
        const string originalReportedPath = "/downloads/original";
        var torrent = new Torrent
        {
            LocalDownloadPath = originalLocalPath,
            ClientReportedDownloadPath = originalReportedPath
        };

        var stored = await AddTorrent("/storage/new", "/downloads/new", torrent);

        Assert.Equal(originalLocalPath, stored.LocalDownloadPath);
        Assert.Equal(originalReportedPath, stored.ClientReportedDownloadPath);
    }

    [Fact]
    public async Task Add_UsesCapturedLocalPathWhenLegacyRetryHasNoReportedPath()
    {
        const string originalLocalPath = "/storage/original";
        var torrent = new Torrent
        {
            LocalDownloadPath = originalLocalPath
        };

        var stored = await AddTorrent("/storage/new", "/downloads/new", torrent);

        Assert.Equal(originalLocalPath, stored.LocalDownloadPath);
        Assert.Equal(originalLocalPath, stored.ClientReportedDownloadPath);
    }

    [Fact]
    public async Task FinalizeRetainedDeletion_ConsumesProviderActionWithoutChangingCompletedOutcome()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var completed = DateTimeOffset.UtcNow.AddMinutes(-1);
        var torrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = Guid.NewGuid().ToString("N"),
            Completed = completed,
            FinishedAction = TorrentFinishedAction.RemoveProvider
        };
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        var torrentData = new TorrentData(dataContext);

        await torrentData.FinalizeRetainedDeletion(torrent.TorrentId, true, true, false);

        var stored = await dataContext.Torrents.AsNoTracking().SingleAsync();
        Assert.True(stored.QbittorrentHidden);
        Assert.Equal(TorrentFinishedAction.None, stored.FinishedAction);
        Assert.Equal(completed, stored.Completed);
        Assert.Null(stored.Error);
    }

    [Fact]
    public async Task Delete_RemovesTorrentAndDownloadsTogether()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = CreateTorrentWithDownload();
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        var torrentData = new TorrentData(dataContext);

        await torrentData.Delete(torrent.TorrentId);

        Assert.Empty(await dataContext.Torrents.AsNoTracking().ToListAsync());
        Assert.Empty(await dataContext.Downloads.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Delete_WhenParentDeleteFails_RollsBackDownloadDeletion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = CreateTorrentWithDownload();
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        await dataContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER RejectTorrentDelete
            BEFORE DELETE ON Torrents
            BEGIN
                SELECT RAISE(ABORT, 'delete rejected');
            END;
            """);
        var torrentData = new TorrentData(dataContext);

        await Assert.ThrowsAsync<DbUpdateException>(() => torrentData.Delete(torrent.TorrentId));

        dataContext.ChangeTracker.Clear();
        Assert.Single(await dataContext.Torrents.AsNoTracking().ToListAsync());
        Assert.Single(await dataContext.Downloads.AsNoTracking().ToListAsync());
    }

    private static Torrent CreateTorrentWithDownload()
    {
        var torrentId = Guid.NewGuid();
        return new()
        {
            TorrentId = torrentId,
            Hash = Guid.NewGuid().ToString("N"),
            Downloads =
            [
                new()
                {
                    DownloadId = Guid.NewGuid(),
                    TorrentId = torrentId,
                    Path = "payload.mkv"
                }
            ]
        };
    }

    private static async Task<Torrent> AddTorrent(
        string currentLocalPath,
        string? currentReportedPath,
        Torrent torrent)
    {
        var originalLocalPath = SettingData.Get.Storage.DownloadPath;
        var originalReportedPath = SettingData.Get.Integrations.ReportedDownloadPath;

        try
        {
            SettingData.Get.Storage.DownloadPath = currentLocalPath;
            SettingData.Get.Integrations.ReportedDownloadPath = currentReportedPath;

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DataContext>()
                         .UseSqlite(connection)
                         .Options;
            await using var dataContext = new DataContext(options);
            await dataContext.Database.EnsureCreatedAsync();
            var torrentData = new TorrentData(dataContext);

            var added = await torrentData.Add(
                null,
                Guid.NewGuid().ToString("N"),
                "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
                false,
                DownloadClient.Internal,
                torrent);

            return await dataContext.Torrents.AsNoTracking().SingleAsync(item => item.TorrentId == added.TorrentId);
        }
        finally
        {
            SettingData.Get.Storage.DownloadPath = originalLocalPath;
            SettingData.Get.Integrations.ReportedDownloadPath = originalReportedPath;
        }
    }
}
