using System.IO.Abstractions.TestingHelpers;
using AdbClient.Data.Data;
using AdbClient.Data.Enums;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.Internal;
using AdbClient.Service.Services;
using AdbClient.Service.Wrappers;
using Microsoft.Extensions.Logging;
using Moq;

namespace AdbClient.Service.Test.Services;

public class ProviderSynchronizationTest
{
    [Fact]
    public async Task RemoveMissingProviderRecords_AutoDeleteRemovesRecordButPreservesLocalFiles()
    {
        var torrent = CreateFinishedTorrent();
        var localFile = Path.Combine(
            Settings.Get.Storage.DownloadPath,
            torrent.Category!,
            torrent.RdName!,
            "movie.mkv");
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [localFile] = new("downloaded media")
        });
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetById(torrent.TorrentId)).ReturnsAsync(torrent);
        var downloads = new Mock<IDownloads>();
        var service = CreateService(torrentData, downloads, fileSystem);

        await service.RemoveMissingProviderRecords(
            [torrent],
            [],
            new DbSettingsProvider { AutoDelete = true });

        Assert.True(fileSystem.File.Exists(localFile));
        downloads.Verify(data => data.DeleteForTorrent(It.IsAny<Guid>()), Times.Never);
        torrentData.Verify(data => data.Delete(torrent.TorrentId), Times.Once);
    }

    [Fact]
    public async Task RemoveMissingProviderRecords_AutoDeleteDisabledLeavesRecordUntouched()
    {
        var torrent = CreateFinishedTorrent();
        var torrentData = new Mock<ITorrentData>();
        var downloads = new Mock<IDownloads>();
        var service = CreateService(torrentData, downloads, new MockFileSystem());

        await service.RemoveMissingProviderRecords(
            [torrent],
            [],
            new DbSettingsProvider { AutoDelete = false });

        torrentData.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);
        downloads.Verify(data => data.DeleteForTorrent(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RemoveMissingProviderRecords_ExistingProviderRecordIsRetained()
    {
        var torrent = CreateFinishedTorrent();
        var torrentData = new Mock<ITorrentData>();
        var downloads = new Mock<IDownloads>();
        var service = CreateService(torrentData, downloads, new MockFileSystem());

        await service.RemoveMissingProviderRecords(
            [torrent],
            [new() { Id = torrent.RdId! }],
            new DbSettingsProvider { AutoDelete = true });

        torrentData.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);
        downloads.Verify(data => data.DeleteForTorrent(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RemoveMissingProviderRecords_QueuedRecordIsRetained()
    {
        var torrent = CreateFinishedTorrent();
        torrent.RdStatus = TorrentStatus.Queued;
        var torrentData = new Mock<ITorrentData>();
        var downloads = new Mock<IDownloads>();
        var service = CreateService(torrentData, downloads, new MockFileSystem());

        await service.RemoveMissingProviderRecords(
            [torrent],
            [],
            new DbSettingsProvider { AutoDelete = true });

        torrentData.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);
        downloads.Verify(data => data.DeleteForTorrent(It.IsAny<Guid>()), Times.Never);
    }

    private static Torrent CreateFinishedTorrent()
    {
        return new()
        {
            TorrentId = Guid.NewGuid(),
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdId = "12345",
            RdName = "Movie.Release",
            Category = "radarr",
            RdStatus = TorrentStatus.Finished,
            Completed = DateTimeOffset.UtcNow
        };
    }

    private static Torrents CreateService(
        Mock<ITorrentData> torrentData,
        Mock<IDownloads> downloads,
        MockFileSystem fileSystem)
    {
        return new(
            Mock.Of<ILogger<Torrents>>(),
            torrentData.Object,
            downloads.Object,
            Mock.Of<IProcessFactory>(),
            fileSystem,
            Mock.Of<IEnricher>(),
            null!);
    }
}
