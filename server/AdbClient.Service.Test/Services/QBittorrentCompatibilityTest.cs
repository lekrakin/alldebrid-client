using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Text;
using System.Text.Json;
using AdbClient.Data.Data;
using AdbClient.Data.Enums;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.Internal;
using AdbClient.Data.Models.TorrentClient;
using AdbClient.Service.Services;
using AdbClient.Service.Services.TorrentClients;
using AdbClient.Service.Wrappers;
using AllDebridNET;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using DownloadClientKind = AdbClient.Data.Enums.DownloadClient;

namespace AdbClient.Service.Test.Services;

[Collection(SettingsIsolationCollection.Name)]
public class QBittorrentCompatibilityTest
{
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(10L)]
    public async Task GetTorrents_ProviderDownloadingDoesNotDependOnLegacySeederCount(long? legacySeeders)
    {
        var torrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdName = "Provider download",
            RdSize = 400,
            RdProgress = 25,
            RdStatus = TorrentStatus.Downloading,
            RdSeeders = legacySeeders
        };
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.Get()).ReturnsAsync([torrent]);
        var compatibility = CreateCompatibility(torrentData: torrentData);

        var info = Assert.Single(await compatibility.GetTorrents(null));

        Assert.Equal("downloading", info.State);
        Assert.Equal(0, info.DownloadSpeed);
    }

    [Fact]
    public async Task GetTorrents_MapsLogposeFieldsAndFiltersCategory()
    {
        var originalMappedPath = Settings.Get.Integrations.ReportedDownloadPath;
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;

        try
        {
            Settings.Get.Integrations.ReportedDownloadPath = "/media/downloads";
            Settings.Get.Storage.DownloadPath = @"D:\Downloads";

            var torrent = new Torrent
            {
                TorrentId = Guid.NewGuid(),
                Hash = "0123456789abcdef0123456789abcdef01234567",
                Category = "logpose",
                RdName = "One Pace Episode 01",
                RdSize = 400,
                RdProgress = 100,
                RdStatus = TorrentStatus.Finished,
                Downloads =
                [
                    new()
                    {
                        FileName = "episode.mkv",
                        Link = "https://example.test/episode.mkv",
                        BytesTotal = 400,
                        BytesDone = 100,
                        Speed = 50
                    }
                ]
            };

            var otherCategory = new Torrent
            {
                TorrentId = Guid.NewGuid(),
                Hash = "fedcba9876543210fedcba9876543210fedcba98",
                Category = "other",
                RdName = "Other"
            };

            var torrentData = new Mock<ITorrentData>();
            torrentData.Setup(data => data.Get()).ReturnsAsync([torrent, otherCategory]);

            var compatibility = CreateCompatibility(torrentData: torrentData);

            var result = await compatibility.GetTorrents("logpose");

            var info = Assert.Single(result);
            Assert.Equal(torrent.Hash, info.Hash);
            Assert.Equal("One Pace Episode 01", info.Name);
            Assert.Equal("logpose", info.Category);
            Assert.Equal(0.625d, info.Progress, 3);
            Assert.Equal("downloading", info.State);
            Assert.Equal("/media/downloads/logpose", info.SavePath);
            Assert.Equal("/media/downloads/logpose/One Pace Episode 01/episode.mkv", info.ContentPath);
            Assert.Equal(400, info.Size);
            Assert.Equal(50, info.DownloadSpeed);
            Assert.Equal(3, info.Eta);
        }
        finally
        {
            Settings.Get.Integrations.ReportedDownloadPath = originalMappedPath;
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task TorrentPaths_RemainBoundToThePathsCapturedWhenAdded()
    {
        var originalMappedPath = Settings.Get.Integrations.ReportedDownloadPath;
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;

        try
        {
            Settings.Get.Integrations.ReportedDownloadPath = "/downloads/new";
            Settings.Get.Storage.DownloadPath = "/storage/new";

            var torrent = new Torrent
            {
                TorrentId = Guid.NewGuid(),
                Hash = "0123456789abcdef0123456789abcdef01234567",
                Category = "radarr",
                RdName = "Movie.Release",
                LocalDownloadPath = "/storage/original",
                ClientReportedDownloadPath = "/downloads/original",
                Downloads =
                [
                    new()
                    {
                        FileName = "movie.mkv",
                        Link = "https://example.test/movie.mkv",
                        Completed = DateTimeOffset.UtcNow
                    }
                ]
            };
            var torrentData = new Mock<ITorrentData>();
            torrentData.Setup(data => data.Get()).ReturnsAsync([torrent]);
            torrentData.Setup(data => data.GetByHash(torrent.Hash)).ReturnsAsync(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData);

            var info = Assert.Single(await compatibility.GetTorrents("radarr"));
            var properties = await compatibility.GetProperties(torrent.Hash);

            Assert.Equal("/downloads/original/radarr", info.SavePath);
            Assert.Equal("/downloads/original/radarr/Movie.Release/movie.mkv", info.ContentPath);
            Assert.Equal("/downloads/original/radarr", properties?.SavePath);
        }
        finally
        {
            Settings.Get.Integrations.ReportedDownloadPath = originalMappedPath;
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task TorrentPaths_UseUpdatedReportedPathWhenPhysicalRootIsUnchanged()
    {
        var originalMappedPath = Settings.Get.Integrations.ReportedDownloadPath;
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;

        try
        {
            Settings.Get.Integrations.ReportedDownloadPath = "/downloads/new";
            Settings.Get.Storage.DownloadPath = "/storage/shared";
            var torrent = new Torrent
            {
                TorrentId = Guid.NewGuid(),
                Hash = "0123456789abcdef0123456789abcdef01234567",
                Category = "sonarr",
                RdName = "Episode",
                LocalDownloadPath = "/storage/shared",
                ClientReportedDownloadPath = "/downloads/old"
            };
            var torrentData = new Mock<ITorrentData>();
            torrentData.Setup(data => data.Get()).ReturnsAsync([torrent]);
            var compatibility = CreateCompatibility(torrentData: torrentData);

            var info = Assert.Single(await compatibility.GetTorrents("sonarr"));

            Assert.Equal("/downloads/new/sonarr", info.SavePath);
        }
        finally
        {
            Settings.Get.Integrations.ReportedDownloadPath = originalMappedPath;
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task GetTorrents_ReportsCompletionOnlyAfterHostDownloadCompletes()
    {
        var torrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = "0123456789abcdef0123456789abcdef01234567",
            Category = "logpose",
            RdName = "One Pace Episode 01",
            RdSize = 400,
            RdProgress = 100,
            RdStatus = TorrentStatus.Finished,
            Completed = DateTimeOffset.UtcNow,
            Downloads =
            [
                new()
                {
                    FileName = "episode.mkv",
                    Link = "https://example.test/episode.mkv",
                    Completed = DateTimeOffset.UtcNow
                }
            ]
        };

        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.Get()).ReturnsAsync([torrent]);

        var compatibility = CreateCompatibility(torrentData: torrentData);
        var info = Assert.Single(await compatibility.GetTorrents("logpose"));

        Assert.Equal(1d, info.Progress);
        Assert.Equal("pausedUP", info.State);
        Assert.Equal(0, info.Eta);
        Assert.Equal(0, info.RatioLimit);
    }

    [Theory]
    [InlineData("release.zip")]
    [InlineData("release.RAR")]
    public async Task GetTorrents_ReportsJobDirectoryForSingleArchive(string fileName)
    {
        var originalMappedPath = Settings.Get.Integrations.ReportedDownloadPath;

        try
        {
            Settings.Get.Integrations.ReportedDownloadPath = "/media/downloads";
            var torrent = new Torrent
            {
                TorrentId = Guid.NewGuid(),
                Hash = "0123456789abcdef0123456789abcdef01234567",
                Category = "radarr",
                RdName = "Movie.Release",
                RdStatus = TorrentStatus.Finished,
                Completed = DateTimeOffset.UtcNow,
                Downloads =
                [
                    new()
                    {
                        FileName = fileName,
                        Link = $"https://example.test/{fileName}",
                        UnpackingFinished = DateTimeOffset.UtcNow,
                        Completed = DateTimeOffset.UtcNow
                    }
                ]
            };
            var torrentData = new Mock<ITorrentData>();
            torrentData.Setup(data => data.Get()).ReturnsAsync([torrent]);
            var compatibility = CreateCompatibility(torrentData: torrentData);

            var info = Assert.Single(await compatibility.GetTorrents("radarr"));

            Assert.Equal("/media/downloads/radarr/Movie.Release", info.ContentPath);
        }
        finally
        {
            Settings.Get.Integrations.ReportedDownloadPath = originalMappedPath;
        }
    }

    [Fact]
    public async Task GetTorrents_FallsBackToJobDirectoryWhenSingleDownloadHasNoFileName()
    {
        var originalMappedPath = Settings.Get.Integrations.ReportedDownloadPath;

        try
        {
            Settings.Get.Integrations.ReportedDownloadPath = "/media/downloads";
            var torrent = new Torrent
            {
                TorrentId = Guid.NewGuid(),
                Hash = "0123456789abcdef0123456789abcdef01234567",
                Category = "sonarr",
                RdName = "Series.Release",
                Downloads =
                [
                    new() { Link = "https://example.test/" }
                ]
            };
            var torrentData = new Mock<ITorrentData>();
            torrentData.Setup(data => data.Get()).ReturnsAsync([torrent]);
            var compatibility = CreateCompatibility(torrentData: torrentData);

            var info = Assert.Single(await compatibility.GetTorrents("sonarr"));

            Assert.Equal("/media/downloads/sonarr/Series.Release", info.ContentPath);
        }
        finally
        {
            Settings.Get.Integrations.ReportedDownloadPath = originalMappedPath;
        }
    }

    [Fact]
    public async Task GetTorrents_KeepsProviderUploadingInDownloadPhase()
    {
        var torrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = "0123456789abcdef0123456789abcdef01234567",
            Category = "radarr",
            RdName = "Movie",
            RdProgress = 100,
            RdStatus = TorrentStatus.Uploading
        };
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.Get()).ReturnsAsync([torrent]);
        var compatibility = CreateCompatibility(torrentData: torrentData);

        var info = Assert.Single(await compatibility.GetTorrents("radarr"));

        Assert.Equal("downloading", info.State);
        Assert.True(info.Progress < 1d);
    }

    [Fact]
    public async Task AddMagnet_UsesExposedDownloadDefaults()
    {
        const string magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=One%20Pace";
        var originalDefaults = Settings.Get.Downloads.Defaults;
        Settings.Get.Downloads.Defaults = new DbSettingsTorrentDefaults
        {
            Category = "from-defaults",
            HostDownloadAction = TorrentHostDownloadAction.DownloadNone,
            FinishedAction = TorrentFinishedAction.RemoveClient,
            FinishedActionDelay = 7,
            MinFileSize = 123,
            IncludeRegex = @"\.mkv$",
            ExcludeRegex = null,
            TorrentRetryAttempts = 4,
            DownloadRetryAttempts = 5,
            DeleteOnError = 6,
            TorrentLifetime = 8,
            Priority = 2
        };

        try
        {
            var torrentData = new Mock<ITorrentData>();
            torrentData.Setup(data => data.GetByHash(It.IsAny<string>())).ReturnsAsync((Torrent?)null);
            torrentData.Setup(data => data.Add(
                           It.IsAny<string?>(),
                           It.IsAny<string>(),
                           It.IsAny<string?>(),
                           It.IsAny<bool>(),
                           It.IsAny<DownloadClientKind>(),
                           It.IsAny<Torrent>()))
                       .ReturnsAsync((string? _, string hash, string? _, bool _, DownloadClientKind _, Torrent torrent) =>
                       {
                           torrent.Hash = hash;
                           return torrent;
                       });

            var enricher = new Mock<IEnricher>();
            enricher.Setup(value => value.EnrichMagnetLink(magnet)).ReturnsAsync(magnet);

            var compatibility = CreateCompatibility(torrentData: torrentData, enricher: enricher);

            await compatibility.Add(magnet, null);

            torrentData.Verify(data => data.Add(
                null,
                It.Is<string>(hash => string.Equals(
                    hash,
                    "0123456789abcdef0123456789abcdef01234567",
                    StringComparison.OrdinalIgnoreCase)),
                magnet,
                false,
                DownloadClientKind.Internal,
                It.Is<Torrent>(torrent =>
                    torrent.Category == "from-defaults" &&
                    torrent.HostDownloadAction == TorrentHostDownloadAction.DownloadNone &&
                    torrent.FinishedAction == TorrentFinishedAction.RemoveClient &&
                    torrent.FinishedActionDelay == 7 &&
                    torrent.DownloadMinSize == 123 &&
                    torrent.IncludeRegex == @"\.mkv$" &&
                    torrent.ExcludeRegex == null &&
                    torrent.TorrentRetryAttempts == 4 &&
                    torrent.DownloadRetryAttempts == 5 &&
                    torrent.DeleteOnError == 6 &&
                    torrent.Lifetime == 8 &&
                    torrent.Priority == 2)), Times.Once);
        }
        finally
        {
            Settings.Get.Downloads.Defaults = originalDefaults;
        }
    }

    [Fact]
    public async Task AddMagnet_RejectsInvalidMetadataBeforeEnrichment()
    {
        const string invalidMagnet = "magnet:?xt=urn:btih:not-a-valid-hash";
        var torrentData = new Mock<ITorrentData>();
        var enricher = new Mock<IEnricher>();
        var compatibility = CreateCompatibility(torrentData: torrentData, enricher: enricher);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            compatibility.Add(invalidMagnet, "radarr"));

        Assert.Equal("Invalid magnet link.", exception.Message);
        enricher.Verify(value => value.EnrichMagnetLink(It.IsAny<string>()), Times.Never);
        torrentData.Verify(data => data.Add(
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<DownloadClientKind>(),
            It.IsAny<Torrent>()), Times.Never);
    }

    [Theory]
    [InlineData("https://nyaa.si/?q=a031cac6baf81b804c4d034dfaef0e5e4a671145")]
    [InlineData("https://nyaa.si/?q=A031CAC6BAF81B804C4D034DFAEF0E5E4A671145")]
    public async Task AddNyaaInfoHashSearchUrl_QueuesCanonicalMagnetAndPreservesCategory(string searchUrl)
    {
        const string infoHash = "a031cac6baf81b804c4d034dfaef0e5e4a671145";
        const string storedHash = "A031CAC6BAF81B804C4D034DFAEF0E5E4A671145";
        const string magnet = $"magnet:?xt=urn:btih:{infoHash}";

        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(storedHash)).ReturnsAsync((Torrent?)null);
        torrentData.Setup(data => data.Add(
                       It.IsAny<string?>(),
                       storedHash,
                       It.IsAny<string?>(),
                       It.IsAny<bool>(),
                       It.IsAny<DownloadClientKind>(),
                       It.IsAny<Torrent>()))
                   .ReturnsAsync((string? _, string hash, string? _, bool _, DownloadClientKind _, Torrent torrent) =>
                   {
                       torrent.Hash = hash;
                       return torrent;
                   });

        var enricher = new Mock<IEnricher>();
        enricher.Setup(value => value.EnrichMagnetLink(magnet)).ReturnsAsync(magnet);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        var compatibility = CreateCompatibility(
            torrentData: torrentData,
            enricher: enricher,
            httpClientFactory: httpClientFactory);

        await compatibility.Add(searchUrl, "logpose");

        enricher.Verify(value => value.EnrichMagnetLink(magnet), Times.Once);
        httpClientFactory.Verify(factory => factory.CreateClient(It.IsAny<string>()), Times.Never);
        torrentData.Verify(data => data.Add(
            null,
            storedHash,
            magnet,
            false,
            DownloadClientKind.Internal,
            It.Is<Torrent>(torrent => torrent.Category == "logpose")), Times.Once);
    }

    [Theory]
    [InlineData("https://nyaa.si/?q=a031cac6baf81b804c4d034dfaef0e5e4a67114")]
    [InlineData("https://nyaa.si/?q=a031cac6baf81b804c4d034dfaef0e5e4a6711450")]
    [InlineData("https://nyaa.si/?q=g031cac6baf81b804c4d034dfaef0e5e4a671145")]
    [InlineData("https://nyaa.si/?q=a031cac6baf81b804c4d034dfaef0e5e4a671145&f=0")]
    public async Task AddNyaaInfoHashSearchUrl_RejectsInvalidQuery(string searchUrl)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var compatibility = CreateCompatibility(httpClientFactory: httpClientFactory);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            compatibility.Add(searchUrl, "logpose"));

        Assert.Contains("Unsupported Nyaa search URL", exception.Message, StringComparison.Ordinal);
        httpClientFactory.Verify(factory => factory.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AddNyaaInfoHashSearchUrl_RetryUsesExistingTorrent()
    {
        const string infoHash = "a031cac6baf81b804c4d034dfaef0e5e4a671145";
        const string storedHash = "A031CAC6BAF81B804C4D034DFAEF0E5E4A671145";
        const string magnet = $"magnet:?xt=urn:btih:{infoHash}";
        const string searchUrl = $"https://nyaa.si/?q={infoHash}";
        var existingTorrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = storedHash,
            Category = "logpose"
        };

        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(storedHash)).ReturnsAsync(existingTorrent);

        var enricher = new Mock<IEnricher>();
        enricher.Setup(value => value.EnrichMagnetLink(magnet)).ReturnsAsync(magnet);

        var compatibility = CreateCompatibility(torrentData: torrentData, enricher: enricher);

        await compatibility.Add(searchUrl, "logpose");

        torrentData.Verify(data => data.Add(
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<DownloadClientKind>(),
            It.IsAny<Torrent>()), Times.Never);
    }

    [Fact]
    public async Task AddRetry_RejectsAssigningCategoryToExistingUncategorizedTorrent()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string magnet = $"magnet:?xt=urn:btih:{hash}";
        var existingTorrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = hash,
            RdStatus = TorrentStatus.Queued
        };
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(It.Is<string>(value =>
                       value.Equals(hash, StringComparison.OrdinalIgnoreCase))))
                   .ReturnsAsync(existingTorrent);
        var enricher = new Mock<IEnricher>();
        enricher.Setup(value => value.EnrichMagnetLink(magnet)).ReturnsAsync(magnet);
        var compatibility = CreateCompatibility(torrentData: torrentData, enricher: enricher);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            compatibility.Add(magnet, "radarr"));

        Assert.Contains("different category", exception.Message, StringComparison.Ordinal);
        torrentData.Verify(data => data.UpdateCategory(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
        torrentData.Verify(data => data.Add(
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<DownloadClientKind>(),
            It.IsAny<Torrent>()), Times.Never);
    }

    [Fact]
    public async Task AddRetry_RejectsExistingTorrentInDifferentCategory()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string magnet = $"magnet:?xt=urn:btih:{hash}";
        var existingTorrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = hash,
            Category = "sonarr",
            RdStatus = TorrentStatus.Queued
        };
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(It.Is<string>(value =>
                       value.Equals(hash, StringComparison.OrdinalIgnoreCase))))
                   .ReturnsAsync(existingTorrent);
        var enricher = new Mock<IEnricher>();
        enricher.Setup(value => value.EnrichMagnetLink(magnet)).ReturnsAsync(magnet);
        var compatibility = CreateCompatibility(torrentData: torrentData, enricher: enricher);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            compatibility.Add(magnet, "radarr"));

        Assert.Contains("different category", exception.Message, StringComparison.Ordinal);
        torrentData.Verify(data => data.UpdateCategory(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task AddRetry_WithoutCategoryUsesExistingCategorizedTorrent()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string magnet = $"magnet:?xt=urn:btih:{hash}";
        var originalDefaultCategory = Settings.Get.Downloads.Defaults.Category;
        var existingTorrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = hash,
            Category = "radarr",
            RdStatus = TorrentStatus.Queued
        };
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(It.Is<string>(value =>
                       value.Equals(hash, StringComparison.OrdinalIgnoreCase))))
                   .ReturnsAsync(existingTorrent);
        var enricher = new Mock<IEnricher>();
        enricher.Setup(value => value.EnrichMagnetLink(magnet)).ReturnsAsync(magnet);
        var compatibility = CreateCompatibility(torrentData: torrentData, enricher: enricher);

        try
        {
            Settings.Get.Downloads.Defaults.Category = null;

            await compatibility.Add(magnet, null);
        }
        finally
        {
            Settings.Get.Downloads.Defaults.Category = originalDefaultCategory;
        }

        torrentData.Verify(data => data.UpdateCategory(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
        torrentData.Verify(data => data.Add(
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<DownloadClientKind>(),
            It.IsAny<Torrent>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentIdenticalAdds_CreateOneTorrentRecord()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string magnet = $"magnet:?xt=urn:btih:{hash}";
        Torrent? storedTorrent = null;
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(It.Is<string>(value =>
                       value.Equals(hash, StringComparison.OrdinalIgnoreCase))))
                   .ReturnsAsync(() => storedTorrent);
        torrentData.Setup(data => data.Add(
                       It.IsAny<string?>(),
                       It.Is<string>(value => value.Equals(hash, StringComparison.OrdinalIgnoreCase)),
                       It.IsAny<string?>(),
                       false,
                       DownloadClientKind.Internal,
                       It.IsAny<Torrent>()))
                   .Returns(async (string? _, string _, string? _, bool _, DownloadClientKind _, Torrent torrent) =>
                   {
                       await Task.Delay(25);
                       torrent.Hash = hash;
                       storedTorrent = torrent;
                       return torrent;
                   });
        var enricher = new Mock<IEnricher>();
        enricher.Setup(value => value.EnrichMagnetLink(magnet)).ReturnsAsync(magnet);
        var compatibility = CreateCompatibility(torrentData: torrentData, enricher: enricher);

        await Task.WhenAll(
            compatibility.Add(magnet, "radarr"),
            compatibility.Add(magnet, "radarr"));

        torrentData.Verify(data => data.Add(
            It.IsAny<string?>(),
            It.Is<string>(value => value.Equals(hash, StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string?>(),
            false,
            DownloadClientKind.Internal,
            It.IsAny<Torrent>()), Times.Once);
    }

    [Fact]
    public async Task ConcurrentIdenticalAddsWithDifferentCategories_CreateOneCategorizedRecordAndRejectConflict()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string magnet = $"magnet:?xt=urn:btih:{hash}";
        Torrent? storedTorrent = null;
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(It.Is<string>(value =>
                       value.Equals(hash, StringComparison.OrdinalIgnoreCase))))
                   .ReturnsAsync(() => storedTorrent);
        torrentData.Setup(data => data.Add(
                       It.IsAny<string?>(),
                       It.Is<string>(value => value.Equals(hash, StringComparison.OrdinalIgnoreCase)),
                       It.IsAny<string?>(),
                       false,
                       DownloadClientKind.Internal,
                       It.IsAny<Torrent>()))
                   .Returns(async (string? _, string _, string? _, bool _, DownloadClientKind _, Torrent torrent) =>
                   {
                       await Task.Delay(25);
                       torrent.Hash = hash;
                       storedTorrent = torrent;
                       return torrent;
                   });
        var enricher = new Mock<IEnricher>();
        enricher.Setup(value => value.EnrichMagnetLink(magnet)).ReturnsAsync(magnet);
        var compatibility = CreateCompatibility(torrentData: torrentData, enricher: enricher);

        var outcomes = await Task.WhenAll(
            Record.ExceptionAsync(() => compatibility.Add(magnet, "radarr")),
            Record.ExceptionAsync(() => compatibility.Add(magnet, "sonarr")));

        Assert.Equal(1, outcomes.Count(exception => exception == null));
        var conflict = Assert.Single(outcomes.OfType<InvalidOperationException>());
        Assert.Contains("different category", conflict.Message, StringComparison.Ordinal);
        Assert.NotNull(storedTorrent);
        Assert.Contains(storedTorrent.Category, new[] { "radarr", "sonarr" });
        torrentData.Verify(data => data.Add(
            It.IsAny<string?>(),
            It.Is<string>(value => value.Equals(hash, StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string?>(),
            false,
            DownloadClientKind.Internal,
            It.Is<Torrent>(torrent => torrent.Category == "radarr" || torrent.Category == "sonarr")), Times.Once);
        torrentData.Verify(data => data.UpdateCategory(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task AddTorrentUrlOnOtherHost_DownloadsAndAddsTorrentFile()
    {
        const string torrentUrl = "https://example.test/?q=a031cac6baf81b804c4d034dfaef0e5e4a671145";
        var torrentBytes = Encoding.Latin1.GetBytes(
            "d4:infod6:lengthi1e4:name11:episode.mkv12:piece lengthi16384e6:pieces20:00000000000000000000ee");

        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(It.IsAny<string>())).ReturnsAsync((Torrent?)null);
        torrentData.Setup(data => data.Add(
                       It.IsAny<string?>(),
                       It.IsAny<string>(),
                       It.IsAny<string?>(),
                       It.IsAny<bool>(),
                       It.IsAny<DownloadClientKind>(),
                       It.IsAny<Torrent>()))
                   .ReturnsAsync((string? _, string hash, string? _, bool _, DownloadClientKind _, Torrent torrent) =>
                   {
                       torrent.Hash = hash;
                       return torrent;
                   });

        var enricher = new Mock<IEnricher>();
        enricher.Setup(value => value.EnrichTorrentBytes(torrentBytes)).ReturnsAsync(torrentBytes);

        var handler = new StubHttpMessageHandler(torrentBytes);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient(It.IsAny<string>()))
                         .Returns(new HttpClient(handler));

        var compatibility = CreateCompatibility(
            torrentData: torrentData,
            enricher: enricher,
            httpClientFactory: httpClientFactory);

        await compatibility.Add(torrentUrl, "logpose");

        Assert.Equal(new Uri(torrentUrl), handler.RequestUri);
        torrentData.Verify(data => data.Add(
            null,
            It.IsAny<string>(),
            It.Is<string>(value => value == Convert.ToBase64String(torrentBytes)),
            true,
            DownloadClientKind.Internal,
            It.Is<Torrent>(torrent => torrent.Category == "logpose")), Times.Once);
    }

    [Fact]
    public async Task AddTorrentFile_UsesDownloadDefaults()
    {
        var torrentBytes = Encoding.Latin1.GetBytes(
            "d4:infod6:lengthi1e4:name11:episode.mkv12:piece lengthi16384e6:pieces20:00000000000000000000ee");
        var originalDefaults = Settings.Get.Downloads.Defaults;
        Settings.Get.Downloads.Defaults = new DbSettingsTorrentDefaults
        {
            Category = "default",
            HostDownloadAction = TorrentHostDownloadAction.DownloadAll,
            FinishedAction = TorrentFinishedAction.None,
            FinishedActionDelay = 4,
            MinFileSize = 512,
            TorrentRetryAttempts = 2,
            DownloadRetryAttempts = 3,
            Priority = 9
        };

        try
        {
            var torrentData = new Mock<ITorrentData>();
            torrentData.Setup(data => data.GetByHash(It.IsAny<string>())).ReturnsAsync((Torrent?)null);
            torrentData.Setup(data => data.Add(
                           It.IsAny<string?>(),
                           It.IsAny<string>(),
                           It.IsAny<string?>(),
                           It.IsAny<bool>(),
                           It.IsAny<DownloadClientKind>(),
                           It.IsAny<Torrent>()))
                       .ReturnsAsync((string? _, string hash, string? _, bool _, DownloadClientKind _, Torrent torrent) =>
                       {
                           torrent.Hash = hash;
                           return torrent;
                       });
            var enricher = new Mock<IEnricher>();
            enricher.Setup(value => value.EnrichTorrentBytes(torrentBytes)).ReturnsAsync(torrentBytes);
            var compatibility = CreateCompatibility(torrentData: torrentData, enricher: enricher);

            await compatibility.Add(torrentBytes, "radarr");

            torrentData.Verify(data => data.Add(
                null,
                It.IsAny<string>(),
                Convert.ToBase64String(torrentBytes),
                true,
                DownloadClientKind.Internal,
                It.Is<Torrent>(torrent =>
                    torrent.Category == "radarr" &&
                    torrent.HostDownloadAction == TorrentHostDownloadAction.DownloadAll &&
                    torrent.FinishedAction == TorrentFinishedAction.None &&
                    torrent.FinishedActionDelay == 4 &&
                    torrent.DownloadMinSize == 512 &&
                    torrent.TorrentRetryAttempts == 2 &&
                    torrent.DownloadRetryAttempts == 3 &&
                    torrent.Priority == 9)), Times.Once);
        }
        finally
        {
            Settings.Get.Downloads.Defaults = originalDefaults;
        }
    }

    [Fact]
    public async Task AddTorrentFile_RejectsInvalidMetadataWithoutPersistingIt()
    {
        var invalidBytes = Encoding.UTF8.GetBytes("not a torrent");
        var torrentData = new Mock<ITorrentData>();
        var enricher = new Mock<IEnricher>();
        enricher.Setup(value => value.EnrichTorrentBytes(invalidBytes)).ReturnsAsync(invalidBytes);
        var compatibility = CreateCompatibility(torrentData: torrentData, enricher: enricher);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            compatibility.Add(invalidBytes, "radarr"));

        Assert.Equal("Invalid torrent file.", exception.Message);
        torrentData.Verify(data => data.Add(
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<DownloadClientKind>(),
            It.IsAny<Torrent>()), Times.Never);
    }

    [Fact]
    public async Task PreferencesAndCategories_UseMappedDownloadPaths()
    {
        var originalMappedPath = Settings.Get.Integrations.ReportedDownloadPath;
        var originalCategories = Settings.Get.Integrations.Categories;

        try
        {
            Settings.Get.Integrations.ReportedDownloadPath = "/media/downloads";
            Settings.Get.Integrations.Categories = "radarr,sonarr";
            var torrentData = new Mock<ITorrentData>();
            torrentData.Setup(data => data.Get()).ReturnsAsync(
            [
                new Torrent
                {
                    TorrentId = Guid.NewGuid(),
                    Hash = "0123456789abcdef0123456789abcdef01234567",
                    Category = "logpose-retained"
                }
            ]);
            var compatibility = CreateCompatibility(torrentData: torrentData);

            var preferences = compatibility.GetPreferences();
            var categories = await compatibility.GetCategories();

            Assert.Equal("/media/downloads", preferences.SavePath);
            Assert.True(preferences.DhtEnabled);
            Assert.True(preferences.QueueingEnabled);
            Assert.False(preferences.MaxRatioEnabled);
            Assert.Equal("/media/downloads/radarr", categories["radarr"].SavePath);
            Assert.Equal("/media/downloads/sonarr", categories["sonarr"].SavePath);
            Assert.Equal("/media/downloads/logpose-retained", categories["logpose-retained"].SavePath);
        }
        finally
        {
            Settings.Get.Integrations.ReportedDownloadPath = originalMappedPath;
            Settings.Get.Integrations.Categories = originalCategories;
        }
    }

    [Theory]
    [InlineData(@"D:\", @"D:\", @"D:\radarr")]
    [InlineData("/", "/", "/radarr")]
    public async Task PreferencesAndCategories_PreserveMappedRoot(
        string mappedPath,
        string expectedRoot,
        string expectedCategoryPath)
    {
        var originalMappedPath = Settings.Get.Integrations.ReportedDownloadPath;
        var originalCategories = Settings.Get.Integrations.Categories;

        try
        {
            Settings.Get.Integrations.ReportedDownloadPath = mappedPath;
            Settings.Get.Integrations.Categories = "radarr";
            var torrentData = new Mock<ITorrentData>();
            torrentData.Setup(data => data.Get()).ReturnsAsync([]);
            var compatibility = CreateCompatibility(torrentData: torrentData);

            Assert.Equal(expectedRoot, compatibility.GetPreferences().SavePath);
            Assert.Equal(expectedCategoryPath, (await compatibility.GetCategories())["radarr"].SavePath);
        }
        finally
        {
            Settings.Get.Integrations.ReportedDownloadPath = originalMappedPath;
            Settings.Get.Integrations.Categories = originalCategories;
        }
    }

    [Fact]
    public async Task PropertiesAndFiles_DescribeTheDownloadedPayload()
    {
        var originalMappedPath = Settings.Get.Integrations.ReportedDownloadPath;

        try
        {
            Settings.Get.Integrations.ReportedDownloadPath = "/media/downloads";
            var torrent = new Torrent
            {
                TorrentId = Guid.NewGuid(),
                Hash = "0123456789abcdef0123456789abcdef01234567",
                Category = "sonarr",
                RdName = "Series.S01",
                RdFiles = JsonSerializer.Serialize(new List<TorrentClientFile>
                {
                    new()
                    {
                        Id = 1,
                        Path = "Season 01/episode.mkv",
                        Bytes = 1_000,
                        Selected = true
                    }
                }),
                Downloads =
                [
                    new()
                    {
                        FileName = "episode.mkv",
                        Link = "https://example.test/episode.mkv"
                    }
                ]
            };
            var torrentData = new Mock<ITorrentData>();
            torrentData.Setup(data => data.GetByHash(torrent.Hash)).ReturnsAsync(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData);

            var properties = await compatibility.GetProperties(torrent.Hash);
            var files = await compatibility.GetFiles(torrent.Hash);

            Assert.NotNull(properties);
            Assert.Equal(torrent.Hash, properties.Hash);
            Assert.Equal("/media/downloads/sonarr", properties.SavePath);
            Assert.Equal("Series.S01/Season 01/episode.mkv", Assert.Single(files!).Name);
        }
        finally
        {
            Settings.Get.Integrations.ReportedDownloadPath = originalMappedPath;
        }
    }

    [Fact]
    public async Task PropertiesAndFiles_ReturnNullForUnknownHash()
    {
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(It.IsAny<string>())).ReturnsAsync((Torrent?)null);
        var compatibility = CreateCompatibility(torrentData: torrentData);

        Assert.Null(await compatibility.GetProperties("missing"));
        Assert.Null(await compatibility.GetFiles("missing"));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("radarr/../outside")]
    [InlineData("radarr//movies")]
    [InlineData("radarr\\movies")]
    [InlineData("C:/outside")]
    [InlineData("radarr/<movies>")]
    [InlineData("radarr,movies")]
    public async Task Add_RejectsUnsafeCategoryPaths(string category)
    {
        const string magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Movie";
        var compatibility = CreateCompatibility();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => compatibility.Add(magnet, category));

        Assert.Contains("Invalid torrent category", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetCategory_RejectsChangesAfterLocalDownloadStarts()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var torrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = hash,
            Downloads = [new() { DownloadId = Guid.NewGuid(), Link = "https://example.test/movie.mkv" }]
        };
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(hash)).ReturnsAsync(torrent);
        var compatibility = CreateCompatibility(torrentData: torrentData);

        await Assert.ThrowsAsync<InvalidOperationException>(() => compatibility.SetCategory(hash, "imported"));
        torrentData.Verify(
            data => data.UpdateCategory(It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task CategoryAndPriorityActions_UpdateEveryRequestedTorrent()
    {
        const string firstHash = "0123456789abcdef0123456789abcdef01234567";
        const string secondHash = "fedcba9876543210fedcba9876543210fedcba98";
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(firstHash)).ReturnsAsync(new Torrent
        {
            TorrentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Hash = firstHash
        });
        torrentData.Setup(data => data.GetByHash(secondHash)).ReturnsAsync(new Torrent
        {
            TorrentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Hash = secondHash
        });
        var compatibility = CreateCompatibility(torrentData: torrentData);

        await compatibility.SetCategory($"{firstHash}|{secondHash}", "sonarr-imported");
        await compatibility.SetTopPriority($"{firstHash}|{secondHash}");

        torrentData.Verify(data => data.UpdateCategory(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "sonarr-imported"), Times.Once);
        torrentData.Verify(data => data.UpdateCategory(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "sonarr-imported"), Times.Once);
        torrentData.Verify(data => data.UpdatePriority(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            1), Times.Once);
        torrentData.Verify(data => data.UpdatePriority(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            1), Times.Once);
    }

    [Fact]
    public async Task CreateCategory_PersistsNewCategoryAndRequestedCasing()
    {
        var originalCategories = Settings.Get.Integrations.Categories;

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<DataContext>()
                          .UseSqlite(connection)
                          .Options;

            await using var dataContext = new DataContext(options);
            await dataContext.Database.EnsureCreatedAsync();
            dataContext.Settings.Add(new Setting
            {
                SettingId = "General:Categories",
                Value = "movies,logpose"
            });
            await dataContext.SaveChangesAsync();

            var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
            await settingData.ResetCache();

            var compatibility = CreateCompatibility(settings: new Settings(settingData));

            await compatibility.CreateCategory("LOGPOSE");
            await compatibility.CreateCategory("anime");

            var stored = await dataContext.Settings.AsNoTracking()
                                          .SingleAsync(setting => setting.SettingId == "General:Categories");
            Assert.Equal("movies,LOGPOSE,anime", stored.Value);
        }
        finally
        {
            Settings.Get.Integrations.Categories = originalCategories;
        }
    }

    [Fact]
    public async Task DeleteWithoutFiles_RetainsLogposeRecordWhenConfiguredActionIsNone()
    {
        var torrentId = Guid.NewGuid();
        var torrent = new Torrent
        {
            TorrentId = torrentId,
            Hash = "0123456789abcdef0123456789abcdef01234567",
            Category = "logpose",
            RdName = "One Pace Episode 01",
            FinishedAction = TorrentFinishedAction.None
        };

        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(torrent.Hash)).ReturnsAsync(torrent);
        torrentData.Setup(data => data.GetById(torrentId)).ReturnsAsync(torrent);
        torrentData.Setup(data => data.Get()).ReturnsAsync([torrent]);
        torrentData.Setup(data => data.FinalizeRetainedDeletion(torrentId, true, false, false))
                   .Callback(() => torrent.QbittorrentHidden = true)
                   .Returns(Task.CompletedTask);

        var downloads = new Mock<IDownloads>();
        var compatibility = CreateCompatibility(torrentData, downloads);

        await compatibility.Delete(torrent.Hash, false);

        Assert.Empty(await compatibility.GetTorrents("logpose"));
        Assert.Empty(await compatibility.GetTorrents("all"));
        Assert.Equal("logpose", torrent.Category);
        Assert.True(torrent.QbittorrentHidden);
        torrentData.Verify(value => value.FinalizeRetainedDeletion(torrentId, true, false, false), Times.Once);
        downloads.Verify(value => value.DeleteForTorrent(It.IsAny<Guid>()), Times.Never);
        torrentData.Verify(value => value.Delete(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData("logpose", TorrentFinishedAction.None, true, false)]
    [InlineData("logpose", TorrentFinishedAction.RemoveProvider, true, true)]
    [InlineData("logpose", TorrentFinishedAction.RemoveClient, false, false)]
    [InlineData("logpose", TorrentFinishedAction.RemoveAllTorrents, false, true)]
    [InlineData("radarr", TorrentFinishedAction.None, true, false)]
    [InlineData("radarr", TorrentFinishedAction.RemoveProvider, true, true)]
    [InlineData("radarr", TorrentFinishedAction.RemoveClient, false, false)]
    [InlineData("radarr", TorrentFinishedAction.RemoveAllTorrents, false, true)]
    public async Task DeleteWithoutFiles_HonorsConfiguredActionForEveryCategory(
        string category,
        TorrentFinishedAction finishedAction,
        bool retainsClientRecord,
        bool removesProviderRecord)
    {
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var jobDirectory = Path.Combine(downloadRoot, category, "Job");
        var localFile = Path.Combine(jobDirectory, "payload.mkv");
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(localFile, new MockFileData("media"));

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent("Job", "payload.mkv");
            torrent.Category = category;
            torrent.FinishedAction = finishedAction;
            torrent.RdId = "123";

            var torrentData = CreateTorrentDataForDelete(torrent);
            var downloads = new Mock<IDownloads>();
            var provider = new Mock<IAllDebridNETClient>();
            var providerMagnets = new Mock<IMagnetApi>();
            provider.SetupGet(client => client.Magnet).Returns(providerMagnets.Object);
            var compatibility = CreateCompatibility(
                torrentData: torrentData,
                downloads: downloads,
                fileSystem: fileSystem,
                allDebridClient: provider);

            await compatibility.Delete(torrent.Hash, false);

            Assert.True(fileSystem.File.Exists(localFile));
            torrentData.Verify(
                data => data.Delete(torrent.TorrentId),
                retainsClientRecord ? Times.Never() : Times.Once());
            downloads.Verify(data => data.DeleteForTorrent(It.IsAny<Guid>()), Times.Never);
            torrentData.Verify(
                data => data.FinalizeRetainedDeletion(
                    torrent.TorrentId,
                    true,
                    finishedAction == TorrentFinishedAction.RemoveProvider,
                    false),
                retainsClientRecord ? Times.Once() : Times.Never());
            Assert.Equal(category, torrent.Category);
            providerMagnets.Verify(
                magnets => magnets.DeleteAsync(torrent.RdId, It.IsAny<CancellationToken>()),
                removesProviderRecord ? Times.Once() : Times.Never());
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithoutFiles_WhenProviderDeleteFails_LeavesRetainedRecordVisibleAndActionUnconsumed()
    {
        var torrent = CreateDeletionTorrent("Job", "payload.mkv");
        torrent.FinishedAction = TorrentFinishedAction.RemoveProvider;
        torrent.RdId = "123";
        var torrentData = CreateTorrentDataForDelete(torrent);
        var provider = new Mock<IAllDebridNETClient>();
        var providerMagnets = new Mock<IMagnetApi>();
        provider.SetupGet(client => client.Magnet).Returns(providerMagnets.Object);
        providerMagnets.Setup(magnets => magnets.DeleteAsync(torrent.RdId, It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new AllDebridException("Provider unavailable", "AUTH_BAD_APIKEY"));
        var compatibility = CreateCompatibility(torrentData: torrentData, allDebridClient: provider);

        await Assert.ThrowsAsync<AllDebridException>(() => compatibility.Delete(torrent.Hash, false));

        Assert.False(torrent.QbittorrentHidden);
        Assert.Equal(TorrentFinishedAction.RemoveProvider, torrent.FinishedAction);
        torrentData.Verify(data => data.FinalizeRetainedDeletion(
            It.IsAny<Guid>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()), Times.Never);
        torrentData.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task DeleteWithoutFiles_WhenProviderTorrentIsAlreadyMissing_HidesRecordAndConsumesAction()
    {
        var torrent = CreateDeletionTorrent("Job", "payload.mkv");
        torrent.FinishedAction = TorrentFinishedAction.RemoveProvider;
        torrent.RdId = "123";
        var completed = torrent.Completed;
        var torrentData = CreateTorrentDataForDelete(torrent);
        var provider = new Mock<IAllDebridNETClient>();
        var providerMagnets = new Mock<IMagnetApi>();
        provider.SetupGet(client => client.Magnet).Returns(providerMagnets.Object);
        providerMagnets.Setup(magnets => magnets.DeleteAsync(torrent.RdId, It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new AllDebridException("Magnet not found", "MAGNET_INVALID_ID"));
        var compatibility = CreateCompatibility(torrentData: torrentData, allDebridClient: provider);

        await compatibility.Delete(torrent.Hash, false);

        Assert.True(torrent.QbittorrentHidden);
        Assert.Equal(TorrentFinishedAction.None, torrent.FinishedAction);
        Assert.Equal(completed, torrent.Completed);
        Assert.Null(torrent.Error);
        torrentData.Verify(data => data.FinalizeRetainedDeletion(torrent.TorrentId, true, true, false), Times.Once);
    }

    [Fact]
    public async Task HiddenTorrent_IsAbsentFromEveryQbittorrentRecordOperation()
    {
        var torrent = CreateDeletionTorrent("Job", "payload.mkv");
        torrent.QbittorrentHidden = true;
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.Get()).ReturnsAsync([torrent]);
        torrentData.Setup(data => data.GetByHash(torrent.Hash)).ReturnsAsync(torrent);
        var compatibility = CreateCompatibility(torrentData: torrentData);

        Assert.Empty(await compatibility.GetTorrents("all"));
        Assert.Null(await compatibility.GetProperties(torrent.Hash));
        Assert.Null(await compatibility.GetFiles(torrent.Hash));
        await compatibility.SetCategory(torrent.Hash, "other");
        await compatibility.SetTopPriority(torrent.Hash);
        await compatibility.Delete(torrent.Hash, true);

        torrentData.Verify(data => data.UpdateCategory(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
        torrentData.Verify(data => data.UpdatePriority(It.IsAny<Guid>(), It.IsAny<int?>()), Times.Never);
        torrentData.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData("logpose", TorrentFinishedAction.None)]
    [InlineData("logpose", TorrentFinishedAction.RemoveProvider)]
    [InlineData("logpose", TorrentFinishedAction.RemoveClient)]
    [InlineData("logpose", TorrentFinishedAction.RemoveAllTorrents)]
    [InlineData("sonarr", TorrentFinishedAction.None)]
    [InlineData("sonarr", TorrentFinishedAction.RemoveProvider)]
    [InlineData("sonarr", TorrentFinishedAction.RemoveClient)]
    [InlineData("sonarr", TorrentFinishedAction.RemoveAllTorrents)]
    public async Task DeleteWithFiles_RemovesOnlyJobPayloadForEveryConfiguredAction(
        string category,
        TorrentFinishedAction finishedAction)
    {
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var categoryRoot = Path.Combine(downloadRoot, category);
        var jobDirectory = Path.Combine(categoryRoot, "Job");
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(Path.Combine(jobDirectory, "payload.mkv"), new MockFileData("media"));

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent("Job", "payload.mkv");
            torrent.Category = category;
            torrent.FinishedAction = finishedAction;

            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, true);

            Assert.False(fileSystem.Directory.Exists(jobDirectory));
            Assert.True(fileSystem.Directory.Exists(categoryRoot));
            Assert.True(fileSystem.Directory.Exists(downloadRoot));
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithFiles_RemovesClientRecord()
    {
        var torrentId = Guid.NewGuid();
        var torrent = new Torrent
        {
            TorrentId = torrentId,
            Hash = "0123456789abcdef0123456789abcdef01234567",
            Category = "logpose",
            RdName = null,
            FinishedAction = TorrentFinishedAction.RemoveAllTorrents
        };

        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(torrent.Hash)).ReturnsAsync(torrent);
        torrentData.Setup(data => data.GetById(torrentId)).ReturnsAsync(torrent);

        var downloads = new Mock<IDownloads>();
        var compatibility = CreateCompatibility(torrentData, downloads);

        await compatibility.Delete(torrent.Hash, true);

        downloads.Verify(value => value.DeleteForTorrent(It.IsAny<Guid>()), Times.Never);
        torrentData.Verify(value => value.Delete(torrentId), Times.Once);
    }

    [Fact]
    public async Task DeleteWithFiles_RemovesSafeJobDirectoryAndPreservesRoots()
    {
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var categoryRoot = Path.Combine(downloadRoot, "radarr");
        var jobDirectory = Path.Combine(categoryRoot, "Movie");
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(Path.Combine(jobDirectory, "movie.mkv"), new MockFileData("media"));

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent("Movie", "movie.mkv");
            torrent.Category = "radarr";
            torrent.FinishedAction = TorrentFinishedAction.RemoveAllTorrents;
            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, true);

            Assert.False(fileSystem.Directory.Exists(jobDirectory));
            Assert.True(fileSystem.Directory.Exists(categoryRoot));
            Assert.True(fileSystem.Directory.Exists(downloadRoot));
            torrentData.Verify(data => data.Delete(torrent.TorrentId), Times.Once);
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithFiles_RejectsCategoryOutsideDownloadRootBeforeMutation()
    {
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var outsideDirectory = Path.GetFullPath(Path.Combine(downloadRoot, "..", "outside-category", "Movie"));
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(Path.Combine(outsideDirectory, "movie.mkv"), new MockFileData("media"));

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent("Movie", "movie.mkv");
            torrent.Category = "../outside-category";
            torrent.FinishedAction = TorrentFinishedAction.RemoveAllTorrents;
            torrent.RdId = "123";
            var torrentData = CreateTorrentDataForDelete(torrent);
            var provider = new Mock<IAllDebridNETClient>();
            var providerMagnets = new Mock<IMagnetApi>();
            provider.SetupGet(client => client.Magnet).Returns(providerMagnets.Object);
            var compatibility = CreateCompatibility(
                torrentData: torrentData,
                fileSystem: fileSystem,
                allDebridClient: provider);

            await Assert.ThrowsAsync<InvalidDataException>(() => compatibility.Delete(torrent.Hash, true));

            Assert.True(fileSystem.Directory.Exists(outsideDirectory));
            torrentData.Verify(data => data.UpdateComplete(
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<bool>()), Times.Never);
            providerMagnets.Verify(
                magnets => magnets.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
            torrentData.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Theory]
    [InlineData("job")]
    [InlineData("category")]
    [InlineData("download")]
    public async Task DeleteWithFiles_RejectsReparsePointBeforeMutation(string reparseLocation)
    {
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var categoryRoot = Path.Combine(downloadRoot, "radarr");
        var jobDirectory = Path.Combine(categoryRoot, "Movie");
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(Path.Combine(jobDirectory, "movie.mkv"), new MockFileData("media"));
        var reparseDirectory = reparseLocation switch
        {
            "job" => jobDirectory,
            "category" => categoryRoot,
            "download" => downloadRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(reparseLocation))
        };
        fileSystem.File.SetAttributes(
            reparseDirectory,
            fileSystem.File.GetAttributes(reparseDirectory) | FileAttributes.ReparsePoint);

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent("Movie", "movie.mkv");
            torrent.Category = "radarr";
            torrent.FinishedAction = TorrentFinishedAction.RemoveAllTorrents;
            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await Assert.ThrowsAsync<InvalidDataException>(() => compatibility.Delete(torrent.Hash, true));

            Assert.True(fileSystem.Directory.Exists(jobDirectory));
            torrentData.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithoutFiles_RemovesEmptySingleFileJobDirectoryAndPreservesRoots()
    {
        const string jobName = "[One Pace][303] Long Ring Long Land 00 [1080p][E85B9E9D].mkv";
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var categoryRoot = Path.Combine(downloadRoot, "logpose");
        var jobDirectory = Path.Combine(categoryRoot, jobName);
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(jobDirectory);

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent(jobName, jobName);
            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, false);

            Assert.False(fileSystem.Directory.Exists(jobDirectory));
            Assert.True(fileSystem.Directory.Exists(categoryRoot));
            Assert.True(fileSystem.Directory.Exists(downloadRoot));
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithoutFiles_CleansCapturedRootAfterGlobalPathChanges()
    {
        const string jobName = "Imported Movie";
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var capturedRoot = GetTestDownloadRoot();
        var currentRoot = Path.Combine(capturedRoot, "new-root");
        var capturedCategoryRoot = Path.Combine(capturedRoot, "radarr");
        var capturedJobRoot = Path.Combine(capturedCategoryRoot, jobName);
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(capturedJobRoot);
        fileSystem.AddDirectory(currentRoot);

        try
        {
            Settings.Get.Storage.DownloadPath = currentRoot;
            var torrent = CreateDeletionTorrent(jobName, "movie.mkv");
            torrent.Category = "radarr";
            torrent.LocalDownloadPath = capturedRoot;
            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, false);

            Assert.False(fileSystem.Directory.Exists(capturedJobRoot));
            Assert.True(fileSystem.Directory.Exists(capturedCategoryRoot));
            Assert.True(fileSystem.Directory.Exists(currentRoot));
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithoutFiles_RemovesNestedEmptyPackDirectoriesBottomUp()
    {
        const string packName = "[One Pace][106-114] Whiskey Peak [480p]";
        const string firstFile = "[One Pace][106-109] Whiskey Peak 01 [480p][AAAAAAAA].mkv";
        const string secondFile = "[One Pace][110-114] Whiskey Peak 02 [480p][BBBBBBBB].mkv";
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var categoryRoot = Path.Combine(downloadRoot, "logpose");
        var jobDirectory = Path.Combine(categoryRoot, packName);
        var nestedDirectory = Path.Combine(jobDirectory, packName);
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(nestedDirectory);

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent(packName, firstFile);
            torrent.Downloads.Add(new()
            {
                DownloadId = Guid.NewGuid(),
                TorrentId = torrent.TorrentId,
                FileName = secondFile,
                Link = "https://example.test/second-file"
            });
            torrent.RdFiles = JsonSerializer.Serialize(new[]
            {
                new TorrentClientFile { Path = $"{packName}/{firstFile}" },
                new TorrentClientFile { Path = $"{packName}/{secondFile}" }
            });

            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, false);

            Assert.False(fileSystem.Directory.Exists(nestedDirectory));
            Assert.False(fileSystem.Directory.Exists(jobDirectory));
            Assert.True(fileSystem.Directory.Exists(categoryRoot));
            Assert.True(fileSystem.Directory.Exists(downloadRoot));
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithoutFiles_PreservesNonEmptyJobDirectory()
    {
        const string jobName = "One Pace Episode 01";
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var jobDirectory = Path.Combine(downloadRoot, "logpose", jobName);
        var retainedFile = Path.Combine(jobDirectory, "keep.mkv");
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(retainedFile, new MockFileData("media"));

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent(jobName, "episode.mkv");
            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, false);

            Assert.True(fileSystem.Directory.Exists(jobDirectory));
            Assert.True(fileSystem.File.Exists(retainedFile));
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithoutFiles_PreservesDirectoryOutsideCategoryRoot()
    {
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var categoryRoot = Path.Combine(downloadRoot, "logpose");
        var outsideDirectory = Path.Combine(downloadRoot, "outside-job");
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(categoryRoot);
        fileSystem.AddDirectory(outsideDirectory);

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent(Path.Combine("..", "outside-job"), "episode.mkv");
            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, false);

            Assert.True(fileSystem.Directory.Exists(outsideDirectory));
            Assert.True(fileSystem.Directory.Exists(categoryRoot));
            Assert.True(fileSystem.Directory.Exists(downloadRoot));
            torrentData.Verify(
                value => value.FinalizeRetainedDeletion(torrent.TorrentId, true, false, false),
                Times.Once);
            torrentData.Verify(value => value.Delete(It.IsAny<Guid>()), Times.Never);
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithoutFiles_SanitizesMetadataTraversalAndPreservesSiblingDirectory()
    {
        const string jobName = "One Pace Episode 01";
        const string siblingName = "other-job";
        const string fileName = "episode.mkv";
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var categoryRoot = Path.Combine(downloadRoot, "logpose");
        var jobDirectory = Path.Combine(categoryRoot, jobName);
        var siblingDirectory = Path.Combine(categoryRoot, siblingName);
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(jobDirectory);
        fileSystem.AddDirectory(siblingDirectory);

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent(jobName, fileName);
            torrent.RdFiles = JsonSerializer.Serialize(new[]
            {
                new TorrentClientFile { Path = $"../{siblingName}/{fileName}" }
            });
            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, false);

            Assert.False(fileSystem.Directory.Exists(jobDirectory));
            Assert.True(fileSystem.Directory.Exists(siblingDirectory));
            torrentData.Verify(
                value => value.FinalizeRetainedDeletion(torrent.TorrentId, true, false, false),
                Times.Once);
            torrentData.Verify(value => value.Delete(It.IsAny<Guid>()), Times.Never);
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Theory]
    [InlineData("job")]
    [InlineData("category")]
    [InlineData("download")]
    public async Task DeleteWithoutFiles_PreservesJobWhenPathContainsReparsePoint(string reparseLocation)
    {
        const string jobName = "One Pace Episode 01";
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var categoryRoot = Path.Combine(downloadRoot, "logpose");
        var jobDirectory = Path.Combine(categoryRoot, jobName);
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(jobDirectory);
        var reparseDirectory = reparseLocation switch
        {
            "job" => jobDirectory,
            "category" => categoryRoot,
            "download" => downloadRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(reparseLocation))
        };
        fileSystem.File.SetAttributes(
            reparseDirectory,
            fileSystem.File.GetAttributes(reparseDirectory) | FileAttributes.ReparsePoint);

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent(jobName, "episode.mkv");
            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, false);

            Assert.True(fileSystem.Directory.Exists(jobDirectory));
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithoutFiles_RemoveAllPolicyRemovesUncategorizedRecordAndPreservesDownloadRoot()
    {
        const string jobName = "One Pace Episode 01";
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var jobDirectory = Path.Combine(downloadRoot, jobName);
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(jobDirectory);

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent(jobName, "episode.mkv");
            torrent.Category = null;
            torrent.FinishedAction = TorrentFinishedAction.RemoveAllTorrents;
            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, false);

            Assert.False(fileSystem.Directory.Exists(jobDirectory));
            Assert.True(fileSystem.Directory.Exists(downloadRoot));
            torrentData.Verify(value => value.Delete(torrent.TorrentId), Times.Once);
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithFiles_RemovesLocalDataAndRetainsRecordWhenConfiguredActionIsNone()
    {
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var categoryRoot = Path.Combine(downloadRoot, "radarr");
        var jobDirectory = Path.Combine(categoryRoot, "Movie");
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(Path.Combine(jobDirectory, "movie.mkv"), new MockFileData("media"));

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent("Movie", "movie.mkv");
            torrent.Category = "radarr";
            torrent.FinishedAction = TorrentFinishedAction.None;
            var torrentData = CreateTorrentDataForDelete(torrent);
            torrentData.Setup(data => data.Get()).ReturnsAsync([torrent]);
            var downloads = new Mock<IDownloads>();
            var compatibility = CreateCompatibility(
                torrentData: torrentData,
                downloads: downloads,
                fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, true);

            Assert.False(fileSystem.Directory.Exists(jobDirectory));
            Assert.True(fileSystem.Directory.Exists(categoryRoot));
            Assert.True(fileSystem.Directory.Exists(downloadRoot));
            Assert.Equal("radarr", torrent.Category);
            Assert.True(torrent.QbittorrentHidden);
            Assert.Empty(await compatibility.GetTorrents("radarr"));
            Assert.Empty(await compatibility.GetTorrents("all"));
            torrentData.Verify(
                data => data.FinalizeRetainedDeletion(torrent.TorrentId, true, false, false),
                Times.Once);
            torrentData.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);
            torrentData.Verify(data => data.UpdateComplete(
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<bool>()), Times.Never);
            downloads.Verify(data => data.DeleteForTorrent(It.IsAny<Guid>()), Times.Never);
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithoutFiles_RetainsLocalDataAndRecordWhenConfiguredActionIsNone()
    {
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var jobDirectory = Path.Combine(downloadRoot, "radarr", "Movie");
        var retainedFile = Path.Combine(jobDirectory, "movie.mkv");
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(retainedFile, new MockFileData("media"));

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent("Movie", "movie.mkv");
            torrent.Category = "radarr";
            torrent.FinishedAction = TorrentFinishedAction.None;
            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, false);

            Assert.True(fileSystem.File.Exists(retainedFile));
            Assert.Equal("radarr", torrent.Category);
            Assert.True(torrent.QbittorrentHidden);
            torrentData.Verify(
                data => data.FinalizeRetainedDeletion(torrent.TorrentId, true, false, false),
                Times.Once);
            torrentData.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);
            torrentData.Verify(data => data.UpdateComplete(
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<bool>()), Times.Never);
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithFiles_RetryIsIdempotentForRetainedRecord()
    {
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var jobDirectory = Path.Combine(downloadRoot, "sonarr", "Episode");
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(Path.Combine(jobDirectory, "episode.mkv"), new MockFileData("media"));

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent("Episode", "episode.mkv");
            torrent.Category = "sonarr";
            torrent.FinishedAction = TorrentFinishedAction.None;
            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, true);
            await compatibility.Delete(torrent.Hash, true);

            Assert.False(fileSystem.Directory.Exists(jobDirectory));
            Assert.Equal("sonarr", torrent.Category);
            Assert.True(torrent.QbittorrentHidden);
            torrentData.Verify(
                data => data.FinalizeRetainedDeletion(torrent.TorrentId, true, false, false),
                Times.Once);
            torrentData.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteAll_IsRejectedBeforeTorrentLookup()
    {
        var torrentData = new Mock<ITorrentData>();
        var compatibility = CreateCompatibility(torrentData: torrentData);

        await Assert.ThrowsAsync<ArgumentException>(() => compatibility.Delete("all", true));

        torrentData.Verify(data => data.GetByHash(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("logpose")]
    [InlineData("radarr")]
    public async Task DeleteWithoutFiles_RetryDoesNotInferDirectoryFromRetainedCategory(string category)
    {
        const string jobName = "Imported Job";
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var jobDirectory = Path.Combine(downloadRoot, category, jobName);
        var importedFile = Path.Combine(jobDirectory, "payload.mkv");
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(importedFile, new MockFileData("media"));

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent(jobName, "payload.mkv");
            torrent.Category = category;
            torrent.FinishedAction = TorrentFinishedAction.None;
            var torrentData = new Mock<ITorrentData>();
            torrentData.Setup(data => data.GetByHash(torrent.Hash)).ReturnsAsync(torrent);
            torrentData.Setup(data => data.FinalizeRetainedDeletion(torrent.TorrentId, true, false, false))
                       .Callback(() => torrent.QbittorrentHidden = true)
                       .Returns(Task.CompletedTask);
            torrentData.Setup(data => data.GetById(torrent.TorrentId)).ReturnsAsync(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, false);
            Assert.True(fileSystem.Directory.Exists(jobDirectory));

            fileSystem.File.Delete(importedFile);
            await compatibility.Delete(torrent.Hash, false);

            Assert.True(fileSystem.Directory.Exists(jobDirectory));
            torrentData.Verify(
                value => value.FinalizeRetainedDeletion(torrent.TorrentId, true, false, false),
                Times.Once);
            torrentData.Verify(value => value.Delete(It.IsAny<Guid>()), Times.Never);
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    [Fact]
    public async Task DeleteWithoutFiles_LegitimateRetainedSuffixDoesNotTargetSiblingCategory()
    {
        const string category = "movies-retained";
        const string jobName = "Same Name";
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        var downloadRoot = GetTestDownloadRoot();
        var categoryJob = Path.Combine(downloadRoot, category, jobName);
        var siblingJob = Path.Combine(downloadRoot, "movies", jobName);
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(categoryJob);
        fileSystem.AddDirectory(siblingJob);

        try
        {
            Settings.Get.Storage.DownloadPath = downloadRoot;
            var torrent = CreateDeletionTorrent(jobName, "payload.mkv");
            torrent.Category = category;
            torrent.FinishedAction = TorrentFinishedAction.None;
            var torrentData = CreateTorrentDataForDelete(torrent);
            var compatibility = CreateCompatibility(torrentData: torrentData, fileSystem: fileSystem);

            await compatibility.Delete(torrent.Hash, false);

            Assert.False(fileSystem.Directory.Exists(categoryJob));
            Assert.True(fileSystem.Directory.Exists(siblingJob));
            torrentData.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
        }
    }

    private static Torrent CreateDeletionTorrent(string rdName, string fileName)
    {
        var torrentId = Guid.NewGuid();

        return new()
        {
            TorrentId = torrentId,
            Hash = Guid.NewGuid().ToString("N"),
            Category = "logpose",
            RdName = rdName,
            Completed = DateTimeOffset.UtcNow,
            Downloads =
            [
                new()
                {
                    DownloadId = Guid.NewGuid(),
                    TorrentId = torrentId,
                    FileName = fileName,
                    Link = "https://example.test/file"
                }
            ]
        };
    }

    private static string GetTestDownloadRoot()
    {
        return Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "adbclient-qbittorrent-tests",
            "downloads"));
    }

    private static Mock<ITorrentData> CreateTorrentDataForDelete(Torrent torrent)
    {
        var torrentData = new Mock<ITorrentData>();
        torrentData.Setup(data => data.GetByHash(torrent.Hash)).ReturnsAsync(torrent);
        torrentData.Setup(data => data.GetById(torrent.TorrentId)).ReturnsAsync(torrent);
        torrentData.Setup(data => data.FinalizeRetainedDeletion(
                       torrent.TorrentId,
                       It.IsAny<bool>(),
                       It.IsAny<bool>(),
                       It.IsAny<bool>()))
                   .Callback<Guid, bool, bool, bool>((_, hide, consume, markAsDeleted) =>
                   {
                       if (hide)
                       {
                           torrent.QbittorrentHidden = true;
                       }

                       if (consume)
                       {
                           torrent.FinishedAction = TorrentFinishedAction.None;
                       }

                       if (markAsDeleted)
                       {
                           torrent.Completed = DateTimeOffset.UtcNow;
                           torrent.Error = "Torrent deleted";
                       }
                   })
                   .Returns(Task.CompletedTask);
        return torrentData;
    }

    private static QBittorrentCompatibility CreateCompatibility(
        Mock<ITorrentData>? torrentData = null,
        Mock<IDownloads>? downloads = null,
        Mock<IEnricher>? enricher = null,
        Mock<IHttpClientFactory>? httpClientFactory = null,
        MockFileSystem? fileSystem = null,
        Settings? settings = null,
        Mock<IAllDebridNETClient>? allDebridClient = null)
    {
        torrentData ??= new();
        downloads ??= new();
        enricher ??= new();
        httpClientFactory ??= new();
        fileSystem ??= new();
        allDebridClient ??= new();

        var allDebridClientFactory = new Mock<IAllDebridNetClientFactory>();
        allDebridClientFactory.Setup(factory => factory.GetClient()).Returns(allDebridClient.Object);
        var allDebridTorrentClient = new AllDebridTorrentClient(
            Mock.Of<ILogger<AllDebridTorrentClient>>(),
            allDebridClientFactory.Object,
            Mock.Of<IDownloadableFileFilter>());

        var processFactory = new Mock<IProcessFactory>();
        var torrents = new Torrents(
            Mock.Of<ILogger<Torrents>>(),
            torrentData.Object,
            downloads.Object,
            processFactory.Object,
            fileSystem,
            enricher.Object,
            allDebridTorrentClient);

        return new(
            Mock.Of<ILogger<QBittorrentCompatibility>>(),
            null!,
            settings!,
            torrents,
            httpClientFactory.Object,
            fileSystem);
    }

    private sealed class StubHttpMessageHandler(byte[] responseBytes) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBytes)
            });
        }
    }
}
