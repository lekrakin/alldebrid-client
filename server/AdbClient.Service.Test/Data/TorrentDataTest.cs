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
    public async Task UpdateRdData_PreservesUnusedLegacySeederColumn()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>().UseSqlite(connection).Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdSeeders = 10
        };
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        var torrentData = new TorrentData(dataContext);

        await torrentData.UpdateRdData(new Torrent
        {
            TorrentId = torrent.TorrentId,
            RdProgress = 25,
            RdStatus = TorrentStatus.Downloading
        });

        dataContext.ChangeTracker.Clear();
        var stored = await dataContext.Torrents.AsNoTracking().SingleAsync();
        Assert.Equal(10, stored.RdSeeders);
        Assert.Equal(25, stored.RdProgress);
        Assert.Equal(TorrentStatus.Downloading, stored.RdStatus);
    }

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
            RdId = "provider-id",
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
        Assert.Null(stored.RdId);
        Assert.Equal(completed, stored.Completed);
        Assert.Null(stored.Error);
    }

    [Fact]
    public async Task FinalizeRetainedDeletion_WithoutProviderDeletionPreservesProviderIdentity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = Guid.NewGuid().ToString("N"),
            RdId = "provider-id",
            Completed = DateTimeOffset.UtcNow,
            FinishedAction = TorrentFinishedAction.None
        };
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        var torrentData = new TorrentData(dataContext);

        await torrentData.FinalizeRetainedDeletion(torrent.TorrentId, true, false, false);

        dataContext.ChangeTracker.Clear();
        var stored = await dataContext.Torrents.AsNoTracking().SingleAsync();
        Assert.True(stored.QbittorrentHidden);
        Assert.Equal("provider-id", stored.RdId);
        Assert.Equal(TorrentFinishedAction.None, stored.FinishedAction);
    }

    [Fact]
    public async Task ReactivateFromQbittorrent_WithoutResetOnlyChangesVisibility()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = CreateRetainedTorrent();
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        var torrentData = new TorrentData(dataContext);
        var requestedDefaults = new Torrent
        {
            FinishedAction = TorrentFinishedAction.RemoveClient,
            DownloadRetryAttempts = 99
        };

        await torrentData.ReactivateFromQbittorrent(torrent.TorrentId, requestedDefaults, null, false);

        dataContext.ChangeTracker.Clear();
        var stored = await dataContext.Torrents
                                      .AsNoTracking()
                                      .Include(value => value.Downloads)
                                      .SingleAsync();
        Assert.False(stored.QbittorrentHidden);
        Assert.Equal(TorrentFinishedAction.RemoveProvider, stored.FinishedAction);
        Assert.Equal(4, stored.DownloadRetryAttempts);
        Assert.NotNull(stored.Completed);
        Assert.NotNull(Assert.Single(stored.Downloads).Completed);
    }

    [Fact]
    public async Task ReactivateFromQbittorrent_WithRetainedProviderRequeuesOnlyMissingRecordsAndAppliesDefaults()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = CreateRetainedTorrent();
        var originalTorrentId = torrent.TorrentId;
        var originalDownloadId = Assert.Single(torrent.Downloads).DownloadId;
        var retainedDownload = new Download
        {
            DownloadId = Guid.NewGuid(),
            TorrentId = torrent.TorrentId,
            Path = "https://example.invalid/retained",
            FileName = "retained.mkv",
            Link = "https://example.invalid/retained-link",
            Added = torrent.Added,
            DownloadFinished = torrent.Completed,
            Completed = torrent.Completed
        };
        torrent.Downloads.Add(retainedDownload);
        var originalAdded = torrent.Added;
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        var torrentData = new TorrentData(dataContext);
        var requestedDefaults = new Torrent
        {
            DownloadClient = DownloadClient.Internal,
            HostDownloadAction = TorrentHostDownloadAction.DownloadAll,
            DownloadAction = TorrentDownloadAction.DownloadManual,
            FinishedAction = TorrentFinishedAction.RemoveClient,
            FinishedActionDelay = 17,
            DownloadMinSize = 23,
            IncludeRegex = "include",
            ExcludeRegex = "exclude",
            DownloadManualFiles = "1,2",
            TorrentRetryAttempts = 3,
            DownloadRetryAttempts = 8,
            DeleteOnError = 11,
            Lifetime = 29,
            Priority = 5
        };

        await torrentData.ReactivateFromQbittorrent(
            torrent.TorrentId, requestedDefaults, new HashSet<Guid> { originalDownloadId }, false);

        dataContext.ChangeTracker.Clear();
        var stored = await dataContext.Torrents
                                      .AsNoTracking()
                                      .Include(value => value.Downloads)
                                      .SingleAsync();
        Assert.Equal(2, stored.Downloads.Count);
        var storedDownload = Assert.Single(stored.Downloads, download => download.DownloadId == originalDownloadId);
        var storedRetainedDownload = Assert.Single(stored.Downloads, download => download.DownloadId == retainedDownload.DownloadId);
        Assert.Equal(retainedDownload.Added, storedRetainedDownload.Added);
        Assert.Equal(retainedDownload.Completed, storedRetainedDownload.Completed);
        Assert.Equal(retainedDownload.DownloadFinished, storedRetainedDownload.DownloadFinished);
        Assert.Equal(retainedDownload.Link, storedRetainedDownload.Link);
        Assert.Equal(originalTorrentId, stored.TorrentId);
        Assert.Equal(originalDownloadId, storedDownload.DownloadId);
        Assert.False(stored.QbittorrentHidden);
        Assert.True(stored.Added >= originalAdded);
        Assert.Null(stored.Completed);
        Assert.Null(stored.Error);
        Assert.Null(stored.Retry);
        Assert.Equal(0, stored.RetryCount);
        Assert.Equal("provider-id", stored.RdId);
        Assert.Equal(TorrentStatus.Finished, stored.RdStatus);
        Assert.NotNull(stored.RdAdded);
        Assert.NotNull(stored.RdEnded);
        Assert.Equal("Ready", stored.RdStatusRaw);
        Assert.Equal("/storage/original", stored.LocalDownloadPath);
        Assert.Equal("/downloads/original", stored.ClientReportedDownloadPath);
        Assert.Equal("radarr", stored.Category);
        Assert.Equal("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", stored.FileOrMagnet);
        Assert.Equal(TorrentHostDownloadAction.DownloadAll, stored.HostDownloadAction);
        Assert.Equal(TorrentDownloadAction.DownloadManual, stored.DownloadAction);
        Assert.Equal(TorrentFinishedAction.RemoveClient, stored.FinishedAction);
        Assert.Equal(17, stored.FinishedActionDelay);
        Assert.Equal(23, stored.DownloadMinSize);
        Assert.Equal("include", stored.IncludeRegex);
        Assert.Equal("exclude", stored.ExcludeRegex);
        Assert.Equal("1,2", stored.DownloadManualFiles);
        Assert.Equal(3, stored.TorrentRetryAttempts);
        Assert.Equal(8, stored.DownloadRetryAttempts);
        Assert.Equal(11, stored.DeleteOnError);
        Assert.Equal(29, stored.Lifetime);
        Assert.Equal(5, stored.Priority);
        Assert.Equal("https://example.invalid/restricted", storedDownload.Path);
        Assert.Equal("payload.mkv", storedDownload.FileName);
        Assert.Null(storedDownload.Link);
        Assert.Null(storedDownload.RemoteId);
        Assert.Null(storedDownload.DownloadStarted);
        Assert.Null(storedDownload.DownloadFinished);
        Assert.Null(storedDownload.UnpackingQueued);
        Assert.Null(storedDownload.UnpackingStarted);
        Assert.Null(storedDownload.UnpackingFinished);
        Assert.Null(storedDownload.Completed);
        Assert.Null(storedDownload.Error);
        Assert.Equal(0, storedDownload.RetryCount);
        Assert.NotNull(storedDownload.DownloadQueued);
    }

    [Fact]
    public async Task ReactivateFromQbittorrent_ParentErrorRecoveryPreservesCompletedDownloads()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>().UseSqlite(connection).Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = CreateRetainedTorrent();
        var download = Assert.Single(torrent.Downloads);
        download.Error = null;
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();

        await new TorrentData(dataContext).ReactivateFromQbittorrent(
            torrent.TorrentId, new Torrent(), new HashSet<Guid>(), false);

        dataContext.ChangeTracker.Clear();
        var stored = await dataContext.Torrents.AsNoTracking().Include(value => value.Downloads).SingleAsync();
        var retained = Assert.Single(stored.Downloads);
        Assert.Null(stored.Error);
        Assert.Null(stored.Completed);
        Assert.False(stored.QbittorrentHidden);
        Assert.Equal("provider-id", stored.RdId);
        Assert.Equal(download.DownloadId, retained.DownloadId);
        Assert.Equal(download.Completed, retained.Completed);
        Assert.Equal(download.DownloadFinished, retained.DownloadFinished);
        Assert.Equal(download.Link, retained.Link);
        Assert.Equal(download.RemoteId, retained.RemoteId);
        Assert.Equal(download.RetryCount, retained.RetryCount);
    }

    [Fact]
    public async Task ReactivateFromQbittorrent_WithDownloadNoneDoesNotQueueOldDownloads()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = CreateRetainedTorrent();
        var originalTorrentId = torrent.TorrentId;
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        var torrentData = new TorrentData(dataContext);
        var requestedDefaults = new Torrent
        {
            HostDownloadAction = TorrentHostDownloadAction.DownloadNone
        };

        await torrentData.ReactivateFromQbittorrent(torrent.TorrentId, requestedDefaults, torrent.Downloads.Select(download => download.DownloadId).ToHashSet(), false);

        dataContext.ChangeTracker.Clear();
        var stored = await dataContext.Torrents
                                      .AsNoTracking()
                                      .Include(value => value.Downloads)
                                      .SingleAsync();
        Assert.Equal(originalTorrentId, stored.TorrentId);
        Assert.False(stored.QbittorrentHidden);
        Assert.Null(stored.Completed);
        Assert.Equal("provider-id", stored.RdId);
        Assert.Equal(TorrentStatus.Finished, stored.RdStatus);
        Assert.Equal(TorrentHostDownloadAction.DownloadNone, stored.HostDownloadAction);
        Assert.Empty(stored.Downloads);
        Assert.Empty(await dataContext.Downloads.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ReactivateFromQbittorrent_WithDeletedProviderDropsStaleDownloadsBeforeRequeue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = CreateRetainedTorrent();
        var originalTorrentId = torrent.TorrentId;
        torrent.RdStatus = TorrentStatus.Error;
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        var torrentData = new TorrentData(dataContext);

        await torrentData.ReactivateFromQbittorrent(torrent.TorrentId, new Torrent(), torrent.Downloads.Select(download => download.DownloadId).ToHashSet(), true);

        dataContext.ChangeTracker.Clear();
        var stored = await dataContext.Torrents
                                      .AsNoTracking()
                                      .Include(value => value.Downloads)
                                      .SingleAsync();
        Assert.Equal(originalTorrentId, stored.TorrentId);
        Assert.False(stored.QbittorrentHidden);
        Assert.Null(stored.Error);
        Assert.Null(stored.RdId);
        Assert.Equal(TorrentStatus.Queued, stored.RdStatus);
        Assert.Null(stored.FilesSelected);
        Assert.Null(stored.RdAdded);
        Assert.Null(stored.RdEnded);
        Assert.Null(stored.RdFiles);
        Assert.Null(stored.RdStatusRaw);
        Assert.Empty(stored.Downloads);
        Assert.Empty(await dataContext.Downloads.AsNoTracking().ToListAsync());
        Assert.NotNull(stored.FileOrMagnet);
    }

    [Fact]
    public async Task ReactivateFromQbittorrent_WithRetainedProviderAndNoDownloadsClearsFileSelection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = CreateRetainedTorrent();
        torrent.Downloads.Clear();
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        var torrentData = new TorrentData(dataContext);

        await torrentData.ReactivateFromQbittorrent(torrent.TorrentId, new Torrent(), torrent.Downloads.Select(download => download.DownloadId).ToHashSet(), false);

        dataContext.ChangeTracker.Clear();
        var stored = await dataContext.Torrents.AsNoTracking().SingleAsync();
        Assert.Equal("provider-id", stored.RdId);
        Assert.Equal(TorrentStatus.Finished, stored.RdStatus);
        Assert.Null(stored.FilesSelected);
        Assert.Empty(await dataContext.Downloads.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ReactivateFromQbittorrent_RejectsMissingMetadataWithoutMutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = CreateRetainedTorrent();
        torrent.RdId = null;
        torrent.FileOrMagnet = null;
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        var torrentData = new TorrentData(dataContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            torrentData.ReactivateFromQbittorrent(torrent.TorrentId, new Torrent(), torrent.Downloads.Select(download => download.DownloadId).ToHashSet(), false));

        dataContext.ChangeTracker.Clear();
        var stored = await dataContext.Torrents
                                      .AsNoTracking()
                                      .Include(value => value.Downloads)
                                      .SingleAsync();
        Assert.True(stored.QbittorrentHidden);
        Assert.NotNull(stored.Completed);
        Assert.NotNull(Assert.Single(stored.Downloads).Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReactivateFromQbittorrent_LegacyRecordUsesIncomingMetadataWithoutReplacingIdentity(bool isFile)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>().UseSqlite(connection).Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = CreateRetainedTorrent();
        torrent.RdId = null;
        torrent.FileOrMagnet = null;
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        var requested = new Torrent
        {
            Category = torrent.Category,
            FileOrMagnet = isFile ? "dG9ycmVudA==" : "magnet:?xt=urn:btih:" + torrent.Hash,
            IsFile = isFile
        };
        var torrentData = new TorrentData(dataContext);

        await torrentData.ReactivateFromQbittorrent(torrent.TorrentId, requested, new HashSet<Guid>(), false);
        await torrentData.ReactivateFromQbittorrent(torrent.TorrentId, requested, new HashSet<Guid>(), false);

        dataContext.ChangeTracker.Clear();
        var stored = await dataContext.Torrents.AsNoTracking().SingleAsync();
        Assert.Equal(torrent.TorrentId, stored.TorrentId);
        Assert.Equal(torrent.Hash, stored.Hash);
        Assert.Equal(torrent.Category, stored.Category);
        Assert.Equal("/storage/original", stored.LocalDownloadPath);
        Assert.Equal("/downloads/original", stored.ClientReportedDownloadPath);
        Assert.Equal(requested.FileOrMagnet, stored.FileOrMagnet);
        Assert.Equal(isFile, stored.IsFile);
        Assert.Null(stored.RdId);
        Assert.Null(stored.Completed);
        Assert.False(stored.QbittorrentHidden);
    }

    [Fact]
    public async Task ReactivateFromQbittorrent_CategoryMismatchIsRejectedWithoutMutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = CreateRetainedTorrent();
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        var torrentData = new TorrentData(dataContext);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            torrentData.ReactivateFromQbittorrent(
                torrent.TorrentId,
                new Torrent { Category = "sonarr" },
                new HashSet<Guid>(),
                false));

        Assert.Contains("different category", exception.Message, StringComparison.Ordinal);
        dataContext.ChangeTracker.Clear();
        var stored = await dataContext.Torrents
                                      .AsNoTracking()
                                      .Include(value => value.Downloads)
                                      .SingleAsync();
        Assert.Equal("radarr", stored.Category);
        Assert.True(stored.QbittorrentHidden);
        Assert.NotNull(stored.Completed);
        Assert.NotNull(Assert.Single(stored.Downloads).Completed);
    }

    [Fact]
    public async Task ReactivateFromQbittorrent_WhenParentUpdateFailsRollsBackDownloadReset()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();
        var torrent = CreateRetainedTorrent();
        dataContext.Torrents.Add(torrent);
        await dataContext.SaveChangesAsync();
        await dataContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER RejectTorrentUpdate
            BEFORE UPDATE ON Torrents
            BEGIN
                SELECT RAISE(ABORT, 'update rejected');
            END;
            """);
        var torrentData = new TorrentData(dataContext);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            torrentData.ReactivateFromQbittorrent(torrent.TorrentId, new Torrent(), torrent.Downloads.Select(download => download.DownloadId).ToHashSet(), false));

        dataContext.ChangeTracker.Clear();
        var stored = await dataContext.Torrents
                                      .AsNoTracking()
                                      .Include(value => value.Downloads)
                                      .SingleAsync();
        var storedDownload = Assert.Single(stored.Downloads);
        Assert.True(stored.QbittorrentHidden);
        Assert.NotNull(stored.Completed);
        Assert.NotNull(storedDownload.Completed);
        Assert.NotNull(storedDownload.Link);
        Assert.NotNull(storedDownload.RemoteId);
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

    private static Torrent CreateRetainedTorrent()
    {
        var torrentId = Guid.NewGuid();
        var completed = DateTimeOffset.UtcNow.AddMinutes(-1);

        return new()
        {
            TorrentId = torrentId,
            Hash = "0123456789abcdef0123456789abcdef01234567",
            Category = "radarr",
            LocalDownloadPath = "/storage/original",
            ClientReportedDownloadPath = "/downloads/original",
            QbittorrentHidden = true,
            DownloadClient = DownloadClient.Internal,
            HostDownloadAction = TorrentHostDownloadAction.DownloadAll,
            DownloadAction = TorrentDownloadAction.DownloadAll,
            FinishedAction = TorrentFinishedAction.RemoveProvider,
            FinishedActionDelay = 2,
            DownloadMinSize = 3,
            TorrentRetryAttempts = 2,
            DownloadRetryAttempts = 4,
            DeleteOnError = 5,
            Lifetime = 6,
            Added = completed.AddMinutes(-10),
            FilesSelected = completed.AddMinutes(-8),
            Completed = completed,
            Retry = completed,
            RetryCount = 2,
            FileOrMagnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            RdId = "provider-id",
            RdName = "retained-job",
            RdStatus = TorrentStatus.Finished,
            RdStatusRaw = "Ready",
            RdAdded = completed.AddMinutes(-9),
            RdEnded = completed.AddMinutes(-2),
            RdProgress = 10000,
            RdSpeed = 100,
            RdSeeders = 10,
            Error = "old error",
            Downloads =
            [
                new()
                {
                    DownloadId = Guid.NewGuid(),
                    TorrentId = torrentId,
                    Path = "https://example.invalid/restricted",
                    Link = "https://example.invalid/unrestricted",
                    FileName = "payload.mkv",
                    RemoteId = "old-remote-id",
                    Added = completed.AddMinutes(-8),
                    DownloadQueued = completed.AddMinutes(-7),
                    DownloadStarted = completed.AddMinutes(-6),
                    DownloadFinished = completed.AddMinutes(-5),
                    UnpackingQueued = completed.AddMinutes(-4),
                    UnpackingStarted = completed.AddMinutes(-3),
                    UnpackingFinished = completed.AddMinutes(-2),
                    Completed = completed,
                    Error = "old download error",
                    RetryCount = 2
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
