using System.Diagnostics;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdbClient.Data.Data;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.Internal;
using AdbClient.Service.Services;
using AdbClient.Service.Services.Downloaders;
using AdbClient.Service.Services.TorrentClients;
using AdbClient.Service.Wrappers;
using AllDebridNET;
using Microsoft.Extensions.Logging;
using Moq;
using DownloadClientKind = AdbClient.Data.Enums.DownloadClient;
using TorrentFinishedAction = AdbClient.Data.Enums.TorrentFinishedAction;
using TorrentHostDownloadAction = AdbClient.Data.Enums.TorrentHostDownloadAction;
using TorrentsService = AdbClient.Service.Services.Torrents;
using TorrentStatus = AdbClient.Data.Enums.TorrentStatus;

namespace AdbClient.Service.Test.Services;

class Mocks
{
    public readonly Mock<IProcessFactory> ProcessFactoryMock;
    public readonly Mock<IProcess> ProcessMock;
    public readonly Mock<ILogger<TorrentsService>> TorrentsLoggerMock;
    public readonly Mock<IDownloads> DownloadsMock;
    public readonly Mock<ITorrentData> TorrentDataMock;
    public readonly Mock<IEnricher> EnricherMock;
    public readonly Mock<IAllDebridNETClient> AllDebridClientMock;
    public readonly Mock<IMagnetApi> AllDebridMagnetsMock;
    public readonly Mock<IAllDebridNetClientFactory> AllDebridClientFactoryMock;

    public Mocks()
    {
        TorrentDataMock = new();
        DownloadsMock = new();
        EnricherMock = new();
        AllDebridClientMock = new();
        AllDebridMagnetsMock = new();
        AllDebridClientFactoryMock = new();
        AllDebridClientMock.SetupGet(client => client.Magnet).Returns(AllDebridMagnetsMock.Object);
        AllDebridClientFactoryMock.Setup(factory => factory.GetClient()).Returns(AllDebridClientMock.Object);

        TorrentsLoggerMock = new();

        ProcessMock = new();
        ProcessStartInfo startInfo = new();
        ProcessMock.SetupProperty(p => p.StartInfo, startInfo);
        ProcessFactoryMock = new();
        ProcessFactoryMock.Setup(p => p.NewProcess()).Returns(ProcessMock.Object);
    }
}

public class TorrentsTest
{
    private const string ExistingHash = "0123456789abcdef0123456789abcdef01234567";
    private const string ExistingMagnet = $"magnet:?xt=urn:btih:{ExistingHash}";

    [Theory]
    [InlineData(true, "include")]
    [InlineData(true, "exclude")]
    [InlineData(false, "include")]
    [InlineData(false, "exclude")]
    public async Task AddToDebridQueue_InvalidFilter_IsRejectedBeforeEnrichment(bool magnet, string filterName)
    {
        var mocks = new Mocks();
        var service = CreateService(mocks);
        var torrent = new Torrent
        {
            IncludeRegex = filterName == "include" ? "[" : null,
            ExcludeRegex = filterName == "exclude" ? "[" : null
        };

        var exception = magnet
            ? await Assert.ThrowsAsync<ArgumentException>(() =>
                service.AddMagnetToDebridQueue(
                    "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
                    torrent))
            : await Assert.ThrowsAsync<ArgumentException>(() =>
                service.AddFileToDebridQueue(Encoding.UTF8.GetBytes("not needed"), torrent));

        Assert.Equal(filterName, exception.ParamName);
        mocks.EnricherMock.VerifyNoOtherCalls();
        mocks.TorrentDataMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AddMagnet_VisibleSameHashRemainsIdempotent()
    {
        var existing = CreateRetainedTorrent(completed: true);
        existing.QbittorrentHidden = false;
        var mocks = CreateMocksForExistingTorrent(existing);
        var service = CreateService(mocks);

        var result = await service.AddMagnetToDebridQueue(
            ExistingMagnet,
            new Torrent { Category = existing.Category });

        Assert.Same(existing, result);
        mocks.TorrentDataMock.Verify(
            data => data.ReactivateFromQbittorrent(
                It.IsAny<Guid>(),
                It.IsAny<Torrent>(),
                It.IsAny<IReadOnlySet<Guid>?>(),
                It.IsAny<bool>()),
            Times.Never);
        mocks.TorrentDataMock.Verify(
            data => data.Add(
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<DownloadClientKind>(),
                It.IsAny<Torrent>()),
            Times.Never);
    }

    [Fact]
    public async Task AddMagnet_HiddenTorrentInDifferentCategoryIsRejected()
    {
        var existing = CreateRetainedTorrent(completed: true);
        var mocks = CreateMocksForExistingTorrent(existing);
        var service = CreateService(mocks);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddMagnetToDebridQueue(
                ExistingMagnet,
                new Torrent { Category = "sonarr" }));

        Assert.Contains("different category", exception.Message, StringComparison.Ordinal);
        mocks.TorrentDataMock.Verify(
            data => data.ReactivateFromQbittorrent(
                It.IsAny<Guid>(),
                It.IsAny<Torrent>(),
                It.IsAny<IReadOnlySet<Guid>?>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task AddMagnet_HiddenIncompleteTorrentIsOnlyUnhidden()
    {
        var existing = CreateRetainedTorrent(completed: false);
        var mocks = CreateMocksForExistingTorrent(existing);
        SetupReactivation(mocks, existing);
        var service = CreateService(mocks);

        var result = await service.AddMagnetToDebridQueue(
            ExistingMagnet,
            new Torrent { Category = existing.Category });

        Assert.False(result.QbittorrentHidden);
        mocks.TorrentDataMock.Verify(
            data => data.ReactivateFromQbittorrent(
                existing.TorrentId,
                It.IsAny<Torrent>(),
                null,
                false),
            Times.Once);
    }

    [Fact]
    public async Task AddMagnet_HiddenActiveTorrentIsOnlyUnhidden()
    {
        var existing = CreateRetainedTorrent(completed: true);
        var download = new Download
        {
            DownloadId = Guid.NewGuid(),
            TorrentId = existing.TorrentId,
            Torrent = existing,
            Path = "https://example.invalid/restricted",
            Link = "https://example.invalid/file.zip",
            FileName = "file.zip"
        };
        existing.Downloads.Add(download);
        var activeClient = new UnpackClient(download, existing.LocalDownloadPath!);
        var mocks = CreateMocksForExistingTorrent(existing);
        SetupReactivation(mocks, existing);
        var service = CreateService(mocks);

        try
        {
            TorrentRunner.ActiveUnpackClients[download.DownloadId] = activeClient;

            await service.AddMagnetToDebridQueue(
                ExistingMagnet,
                new Torrent { Category = existing.Category });

            mocks.TorrentDataMock.Verify(
                data => data.ReactivateFromQbittorrent(
                    existing.TorrentId,
                    It.IsAny<Torrent>(),
                    null,
                    false),
                Times.Once);
        }
        finally
        {
            activeClient.Cancel();
            TorrentRunner.ActiveUnpackClients.TryRemove(download.DownloadId, out _);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddMagnet_HiddenCompletedTorrentWithNestedPayloadPreservesDownloads(bool parentError)
    {
        var existing = CreateRetainedTorrent(completed: true);
        existing.Error = parentError ? "completion failed" : null;
        AddExpectedDownload(existing, "payload.mkv");
        existing.RdFiles = JsonSerializer.Serialize(new[] { new { Path = "nested/payload.mkv" } });
        var jobRoot = Path.Combine(
            existing.LocalDownloadPath!,
            existing.Category!,
            existing.RdName!);
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(jobRoot, "nested", "payload.mkv")] = new("payload")
        });
        var mocks = CreateMocksForExistingTorrent(existing);
        SetupReactivation(mocks, existing);
        var service = CreateService(mocks, fileSystem: fileSystem);

        await service.AddMagnetToDebridQueue(
            ExistingMagnet,
            new Torrent { Category = existing.Category });

        mocks.TorrentDataMock.Verify(
            data => data.ReactivateFromQbittorrent(
                existing.TorrentId,
                It.IsAny<Torrent>(),
                It.Is<IReadOnlySet<Guid>?>(ids => parentError ? ids != null && ids.Count == 0 : ids == null),
                false),
            Times.Once);
        mocks.AllDebridMagnetsMock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddMagnet_HiddenTorrentRequeuesMissingFilesWithoutCountingSidecars(bool retainFirstFile)
    {
        var existing = CreateRetainedTorrent(completed: true);
        var first = AddExpectedDownload(existing, "first.mkv");
        var second = AddExpectedDownload(existing, "second.mkv");
        var jobRoot = Path.Combine(existing.LocalDownloadPath!, existing.Category!, existing.RdName!);
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(jobRoot, "release.nfo")] = new("metadata")
        });
        if (retainFirstFile)
        {
            fileSystem.AddFile(Path.Combine(jobRoot, "first.mkv"), new("payload"));
        }
        var originalFiles = fileSystem.AllFiles.ToArray();
        var originalDirectories = fileSystem.AllDirectories.ToArray();
        var mocks = CreateMocksForExistingTorrent(existing);
        SetupReactivation(mocks, existing);
        var service = CreateService(mocks, fileSystem: fileSystem);

        await service.AddMagnetToDebridQueue(ExistingMagnet, new Torrent { Category = existing.Category });
        await service.AddMagnetToDebridQueue(ExistingMagnet, new Torrent { Category = existing.Category });

        mocks.TorrentDataMock.Verify(data => data.ReactivateFromQbittorrent(
            existing.TorrentId,
            It.IsAny<Torrent>(),
            It.Is<IReadOnlySet<Guid>?>(ids => ids != null && ids.Contains(second.DownloadId) &&
                ids.Contains(first.DownloadId) == !retainFirstFile && ids.Count == (retainFirstFile ? 1 : 2)),
            false), Times.Once);
        mocks.AllDebridMagnetsMock.VerifyNoOtherCalls();
        Assert.Equal(originalFiles, fileSystem.AllFiles);
        Assert.Equal(originalDirectories, fileSystem.AllDirectories);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "download failed")]
    public async Task AddMagnet_ExistingUnfinishedOrFailedFileIsRequeued(bool completed, string? error)
    {
        var existing = CreateRetainedTorrent(completed: true);
        var download = AddExpectedDownload(existing, "payload.mkv");
        download.Completed = completed ? DateTimeOffset.UtcNow : null;
        download.Error = error;
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(existing.LocalDownloadPath!, existing.Category!, existing.RdName!, "payload.mkv")] = new("partial")
        });
        var mocks = CreateMocksForExistingTorrent(existing);
        SetupReactivation(mocks, existing);

        await CreateService(mocks, fileSystem: fileSystem).AddMagnetToDebridQueue(
            ExistingMagnet, new Torrent { Category = existing.Category });

        mocks.TorrentDataMock.Verify(data => data.ReactivateFromQbittorrent(
            existing.TorrentId,
            It.IsAny<Torrent>(),
            It.Is<IReadOnlySet<Guid>?>(ids => ids != null && ids.SetEquals(new[] { download.DownloadId })),
            false), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddMagnet_LegacyRetainedTorrentUsesIncomingMetadataWhenProviderNeedsRetry(bool providerError)
    {
        var existing = CreateRetainedTorrent(completed: true);
        existing.FileOrMagnet = null;
        existing.RdId = providerError ? "provider-id" : null;
        existing.RdStatus = providerError ? TorrentStatus.Error : TorrentStatus.Finished;
        var mocks = CreateMocksForExistingTorrent(existing);
        SetupReactivation(mocks, existing);

        await CreateService(mocks).AddMagnetToDebridQueue(ExistingMagnet, new Torrent { Category = existing.Category });

        mocks.TorrentDataMock.Verify(data => data.ReactivateFromQbittorrent(
            existing.TorrentId,
            It.Is<Torrent>(requested => requested.FileOrMagnet == ExistingMagnet && !requested.IsFile),
            It.Is<IReadOnlySet<Guid>?>(ids => ids != null),
            providerError), Times.Once);
        mocks.AllDebridMagnetsMock.Verify(magnets => magnets.DeleteAsync(
            "provider-id", It.IsAny<CancellationToken>()), providerError ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task AddFile_LegacyRetainedTorrentUsesIncomingTorrentMetadata()
    {
        var bytes = Encoding.ASCII.GetBytes(
            "d4:infod6:lengthi1e4:name11:episode.mkv12:piece lengthi16384e6:pieces20:00000000000000000000ee");
        var parsed = await MonoTorrent.Torrent.LoadAsync(bytes);
        var existing = CreateRetainedTorrent(completed: true);
        existing.Hash = parsed.InfoHashes.V1OrV2.ToHex();
        existing.FileOrMagnet = null;
        existing.RdId = null;
        var mocks = new Mocks();
        mocks.EnricherMock.Setup(value => value.EnrichTorrentBytes(bytes)).ReturnsAsync(bytes);
        mocks.TorrentDataMock.Setup(data => data.GetByHash(existing.Hash)).ReturnsAsync(existing);
        SetupReactivation(mocks, existing);

        await CreateService(mocks).AddFileToDebridQueue(bytes, new Torrent { Category = existing.Category });

        mocks.TorrentDataMock.Verify(data => data.ReactivateFromQbittorrent(
            existing.TorrentId,
            It.Is<Torrent>(requested => requested.FileOrMagnet == Convert.ToBase64String(bytes) && requested.IsFile),
            It.Is<IReadOnlySet<Guid>?>(ids => ids != null),
            false), Times.Once);
        mocks.AllDebridMagnetsMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AddMagnet_ExpectedPayloadReparsePointIsRejectedWithoutMutation()
    {
        var existing = CreateRetainedTorrent(completed: true);
        AddExpectedDownload(existing, "payload.mkv");
        var path = Path.Combine(existing.LocalDownloadPath!, existing.Category!, existing.RdName!, "payload.mkv");
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [path] = new("payload")
        });
        fileSystem.File.SetAttributes(path, FileAttributes.ReparsePoint);
        var mocks = CreateMocksForExistingTorrent(existing);

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateService(mocks, fileSystem: fileSystem)
            .AddMagnetToDebridQueue(ExistingMagnet, new Torrent { Category = existing.Category }));

        Assert.True(existing.QbittorrentHidden);
        mocks.TorrentDataMock.Verify(data => data.ReactivateFromQbittorrent(
            It.IsAny<Guid>(), It.IsAny<Torrent>(), It.IsAny<IReadOnlySet<Guid>?>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddMagnet_HiddenTerminalErrorWithPartialPayloadIsReset(bool providerError)
    {
        var existing = CreateRetainedTorrent(completed: true);
        existing.Error = providerError ? null : "local download failed";
        existing.RdStatus = providerError ? TorrentStatus.Error : TorrentStatus.Finished;
        var jobRoot = Path.Combine(
            existing.LocalDownloadPath!,
            existing.Category!,
            existing.RdName!);
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(jobRoot, "partial.mkv")] = new("partial")
        });
        var mocks = CreateMocksForExistingTorrent(existing);
        mocks.TorrentDataMock.Setup(data => data.ReactivateFromQbittorrent(
                                  existing.TorrentId,
                                  It.IsAny<Torrent>(),
                                  It.Is<IReadOnlySet<Guid>?>(ids => ids != null),
                                  providerError))
             .Callback(() =>
             {
                 existing.QbittorrentHidden = false;
                 existing.Error = null;

                 if (providerError)
                 {
                     existing.RdId = null;
                     existing.RdStatus = TorrentStatus.Queued;
                 }
             })
             .ReturnsAsync(existing);
        var service = CreateService(mocks, fileSystem: fileSystem);

        var result = await service.AddMagnetToDebridQueue(
            ExistingMagnet,
            new Torrent { Category = existing.Category });

        Assert.False(result.QbittorrentHidden);
        Assert.Null(result.Error);
        mocks.TorrentDataMock.Verify(
            data => data.ReactivateFromQbittorrent(
                existing.TorrentId,
                It.IsAny<Torrent>(),
                It.Is<IReadOnlySet<Guid>?>(ids => ids != null),
                providerError),
            Times.Once);
        mocks.AllDebridMagnetsMock.Verify(
            magnets => magnets.DeleteAsync(
                existing.RdId ?? "provider-id",
                It.IsAny<CancellationToken>()),
            providerError ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddMagnet_HiddenCompletedTorrentWithoutPayloadIsReset(bool createEmptyDirectories)
    {
        var existing = CreateRetainedTorrent(completed: true);
        var fileSystem = new MockFileSystem();

        if (createEmptyDirectories)
        {
            fileSystem.AddDirectory(Path.Combine(
                existing.LocalDownloadPath!,
                existing.Category!,
                existing.RdName!,
                "empty",
                "nested"));
        }

        var mocks = CreateMocksForExistingTorrent(existing);
        SetupReactivation(mocks, existing);
        var service = CreateService(mocks, fileSystem: fileSystem);
        var requested = new Torrent
        {
            Category = existing.Category,
            HostDownloadAction = TorrentHostDownloadAction.DownloadNone,
            FinishedAction = TorrentFinishedAction.RemoveClient,
            DownloadRetryAttempts = 7
        };

        await service.AddMagnetToDebridQueue(ExistingMagnet, requested);

        mocks.TorrentDataMock.Verify(
            data => data.ReactivateFromQbittorrent(
                existing.TorrentId,
                It.Is<Torrent>(torrent =>
                    torrent.HostDownloadAction == TorrentHostDownloadAction.DownloadNone &&
                    torrent.FinishedAction == TorrentFinishedAction.RemoveClient &&
                    torrent.DownloadRetryAttempts == 7),
                It.Is<IReadOnlySet<Guid>?>(ids => ids != null),
                false),
            Times.Once);
    }

    [Fact]
    public async Task AddMagnet_HiddenCompletedTorrentWithUnsafePathIsRejectedWithoutMutation()
    {
        var existing = CreateRetainedTorrent(completed: true);
        existing.Category = "../outside";
        var mocks = CreateMocksForExistingTorrent(existing);
        var service = CreateService(mocks);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.AddMagnetToDebridQueue(
                ExistingMagnet,
                new Torrent { Category = existing.Category }));

        Assert.True(existing.QbittorrentHidden);
        mocks.TorrentDataMock.Verify(
            data => data.ReactivateFromQbittorrent(
                It.IsAny<Guid>(),
                It.IsAny<Torrent>(),
                It.IsAny<IReadOnlySet<Guid>?>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task AddMagnet_HiddenCompletedLegacyTorrentWithoutCapturedPathIsNotGuessed()
    {
        var existing = CreateRetainedTorrent(completed: true);
        existing.LocalDownloadPath = null;
        var mocks = CreateMocksForExistingTorrent(existing);
        var service = CreateService(mocks);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.AddMagnetToDebridQueue(
                ExistingMagnet,
                new Torrent { Category = existing.Category }));

        Assert.Contains("no captured local download path", exception.Message, StringComparison.Ordinal);
        Assert.True(existing.QbittorrentHidden);
        mocks.TorrentDataMock.Verify(
            data => data.ReactivateFromQbittorrent(
                It.IsAny<Guid>(),
                It.IsAny<Torrent>(),
                It.IsAny<IReadOnlySet<Guid>?>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task AddMagnet_HiddenCompletedTorrentWithReparsePointIsRejectedWithoutMutation()
    {
        var existing = CreateRetainedTorrent(completed: true);
        var jobRoot = Path.Combine(
            existing.LocalDownloadPath!,
            existing.Category!,
            existing.RdName!);
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(jobRoot);
        fileSystem.File.SetAttributes(
            jobRoot,
            fileSystem.File.GetAttributes(jobRoot) | FileAttributes.ReparsePoint);
        var mocks = CreateMocksForExistingTorrent(existing);
        var service = CreateService(mocks, fileSystem: fileSystem);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.AddMagnetToDebridQueue(
                ExistingMagnet,
                new Torrent { Category = existing.Category }));

        Assert.True(existing.QbittorrentHidden);
        mocks.TorrentDataMock.Verify(
            data => data.ReactivateFromQbittorrent(
                It.IsAny<Guid>(),
                It.IsAny<Torrent>(),
                It.IsAny<IReadOnlySet<Guid>?>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task AddMagnet_HiddenCompletedTorrentWithUnreadablePathIsRejectedWithoutMutation()
    {
        var existing = CreateRetainedTorrent(completed: true);
        var baseFileSystem = new MockFileSystem();
        var file = new Mock<IFile>();
        file.Setup(value => value.GetAttributes(It.IsAny<string>()))
            .Throws(new UnauthorizedAccessException("denied"));
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.SetupGet(value => value.Path).Returns(baseFileSystem.Path);
        fileSystem.SetupGet(value => value.Directory).Returns(baseFileSystem.Directory);
        fileSystem.SetupGet(value => value.File).Returns(file.Object);
        var mocks = CreateMocksForExistingTorrent(existing);
        var service = CreateService(mocks, fileSystem: fileSystem.Object);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.AddMagnetToDebridQueue(
                ExistingMagnet,
                new Torrent { Category = existing.Category }));

        Assert.True(existing.QbittorrentHidden);
        mocks.TorrentDataMock.Verify(
            data => data.ReactivateFromQbittorrent(
                It.IsAny<Guid>(),
                It.IsAny<Torrent>(),
                It.IsAny<IReadOnlySet<Guid>?>(),
                It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task AddMagnet_ConcurrentHiddenRetriesReactivateOnce()
    {
        var existing = CreateRetainedTorrent(completed: true);
        var mocks = CreateMocksForExistingTorrent(existing);
        mocks.TorrentDataMock.Setup(data => data.ReactivateFromQbittorrent(
                                  existing.TorrentId,
                                  It.IsAny<Torrent>(),
                                  It.Is<IReadOnlySet<Guid>?>(ids => ids != null),
                                  false))
                             .Returns(async () =>
                             {
                                 await Task.Delay(25);
                                 existing.QbittorrentHidden = false;
                                 return existing;
                             });
        var service = CreateService(mocks);

        await Task.WhenAll(
            service.AddMagnetToDebridQueue(
                ExistingMagnet,
                new Torrent { Category = existing.Category }),
            service.AddMagnetToDebridQueue(
                ExistingMagnet,
                new Torrent { Category = existing.Category }));

        mocks.TorrentDataMock.Verify(
            data => data.ReactivateFromQbittorrent(
                existing.TorrentId,
                It.IsAny<Torrent>(),
                It.Is<IReadOnlySet<Guid>?>(ids => ids != null),
                false),
            Times.Once);
        mocks.TorrentDataMock.Verify(
            data => data.Add(
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<DownloadClientKind>(),
                It.IsAny<Torrent>()),
            Times.Never);
    }

    [Fact]
    public async Task AddMagnet_WaitsForConcurrentQbittorrentDeletionThenReactivates()
    {
        var existing = CreateRetainedTorrent(completed: true);
        existing.QbittorrentHidden = false;
        var deletionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDeletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mocks = CreateMocksForExistingTorrent(existing);
        mocks.TorrentDataMock.Setup(data => data.GetById(existing.TorrentId))
             .ReturnsAsync(existing);
        mocks.TorrentDataMock.Setup(data => data.FinalizeRetainedDeletion(
                                  existing.TorrentId,
                                  true,
                                  false,
                                  false))
             .Returns(async () =>
             {
                 deletionStarted.TrySetResult();
                 await allowDeletion.Task;
                 existing.QbittorrentHidden = true;
             });
        SetupReactivation(mocks, existing);
        var service = CreateService(mocks);

        try
        {
            var deletion = service.Delete(existing.TorrentId, false, false, false, true);
            await deletionStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var addition = service.AddMagnetToDebridQueue(
                ExistingMagnet,
                new Torrent { Category = existing.Category });
            var earlyCompletion = await Task.WhenAny(addition, Task.Delay(50));

            Assert.NotSame(addition, earlyCompletion);

            allowDeletion.TrySetResult();
            await Task.WhenAll(deletion, addition);

            Assert.False(existing.QbittorrentHidden);
            mocks.TorrentDataMock.Verify(
                data => data.ReactivateFromQbittorrent(
                    existing.TorrentId,
                    It.IsAny<Torrent>(),
                    It.Is<IReadOnlySet<Guid>?>(ids => ids != null),
                    false),
                Times.Once);
        }
        finally
        {
            allowDeletion.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddMagnet_WaitsForConcurrentCategoryMutationThenRejects(bool fullUpdate)
    {
        var existing = CreateRetainedTorrent(completed: false);
        existing.QbittorrentHidden = false;
        var mutationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mocks = CreateMocksForExistingTorrent(existing);

        async Task ApplyCategoryMutation()
        {
            mutationStarted.TrySetResult();
            await allowMutation.Task;
            existing.Category = "sonarr";
        }

        mocks.TorrentDataMock.Setup(data => data.UpdateCategory(existing.TorrentId, "sonarr"))
             .Returns(ApplyCategoryMutation);
        mocks.TorrentDataMock.Setup(data => data.Update(It.Is<Torrent>(torrent =>
                                  torrent.TorrentId == existing.TorrentId &&
                                  torrent.Category == "sonarr")))
             .Returns(ApplyCategoryMutation);
        var service = CreateService(mocks);

        try
        {
            Task mutation = fullUpdate
                ? service.Update(new Torrent
                {
                    TorrentId = existing.TorrentId,
                    Category = "sonarr"
                })
                : service.UpdateCategory(existing.Hash, "sonarr");
            await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var addition = service.AddMagnetToDebridQueue(
                ExistingMagnet,
                new Torrent { Category = "radarr" });
            var earlyCompletion = await Task.WhenAny(addition, Task.Delay(50));

            Assert.NotSame(addition, earlyCompletion);

            allowMutation.TrySetResult();
            await mutation;

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => addition);
            Assert.Contains("different category", exception.Message, StringComparison.Ordinal);
            mocks.TorrentDataMock.Verify(
                data => data.ReactivateFromQbittorrent(
                    It.IsAny<Guid>(),
                    It.IsAny<Torrent>(),
                    It.IsAny<IReadOnlySet<Guid>?>(),
                    It.IsAny<bool>()),
                Times.Never);
        }
        finally
        {
            allowMutation.TrySetResult();
        }
    }

    public static TheoryData<Torrent, List<Download>> TorrentAndDownload()
    {
        var torrent = new Torrent
        {
            RdName = "TestTorrent",
            Hash = "123ABC",
            Category = "Movies",
            RdSize = 100,
            TorrentId = Guid.Empty
        };

        List<Download> downloads =
        [
            new()
            {
                FileName = "file.txt",
                TorrentId = torrent.TorrentId
            }
        ];

        return new()
        {
            {
              torrent,
              downloads
            }
        };
    }

    [Theory]
    [MemberData(nameof(TorrentAndDownload))]
    public async Task RunTorrentComplete_WhenCommandSet_ShouldRunCommand(Torrent torrent, List<Download> downloads)
    {
        // Arrange
        var baseDownloadPath = Path.Combine(Path.GetTempPath(), "adb-test-downloads");
        var settings = new DbSettings
        {
            Integrations = new()
            {
                CompletionCommand = new()
                {
                    ExecutablePath = "/bin/echo",
                    Arguments = "%N %L %F %R %D %C %Z %I"
                }
            },
            Storage = new() { DownloadPath = baseDownloadPath }
        };

        var mocks = new Mocks();

        mocks.TorrentDataMock.Setup(t => t.GetById(torrent.TorrentId)).Returns(Task.FromResult<Torrent?>(torrent));
        mocks.DownloadsMock.Setup(d => d.GetForTorrent(torrent.TorrentId)).ReturnsAsync(downloads);

        var downloadPath = Path.Combine(baseDownloadPath, torrent.Category!);
        var torrentPath = Path.Combine(downloadPath, torrent.RdName!);
        var filePath = Path.Combine(torrentPath, downloads[0].FileName!);

        var fileSystemMock = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            {
                filePath, new("Test file")
            },
        });

        var torrents = new TorrentsService(mocks.TorrentsLoggerMock.Object,
                                           mocks.TorrentDataMock.Object,
                                           mocks.DownloadsMock.Object,
                                           mocks.ProcessFactoryMock.Object,
                                           fileSystemMock,
                                           mocks.EnricherMock.Object,
                                           null!); // AllDebridTorrentClient not used by RunTorrentComplete

        mocks.ProcessMock.Setup(p => p.WaitForExit(It.IsAny<int>())).Returns(true);

        // Act
        await torrents.RunTorrentComplete(torrent.TorrentId, settings);

        // Assert
        Assert.Equal("/bin/echo", mocks.ProcessMock.Object.StartInfo.FileName);

        var expectedArgumentsSb = new StringBuilder();
        expectedArgumentsSb.Append($"\"{torrent.RdName}\"");
        expectedArgumentsSb.Append($" \"{torrent.Category}\"");
        expectedArgumentsSb.Append($" \"{filePath}\"");
        expectedArgumentsSb.Append($" \"{downloadPath}\"");
        expectedArgumentsSb.Append($" \"{torrentPath}\"");
        expectedArgumentsSb.Append($" {downloads.Count.ToString()}");
        expectedArgumentsSb.Append($" {torrent.RdSize.ToString()}");
        expectedArgumentsSb.Append($" {torrent.Hash}");

        var expectedArguments = expectedArgumentsSb.ToString();

        Assert.Equal(expectedArguments, mocks.ProcessMock.Object.StartInfo.Arguments);

        mocks.ProcessMock.Verify(p => p.Start(), Times.Once);
        mocks.ProcessMock.Verify(p => p.WaitForExit(60_000), Times.Once);
    }

    [Theory]
    [MemberData(nameof(TorrentAndDownload))]
    public async Task RunTorrentComplete_WhenCommandTimesOut_TerminatesProcessTree(
        Torrent torrent,
        List<Download> downloads)
    {
        var baseDownloadPath = Path.Combine(Path.GetTempPath(), "adb-test-downloads");
        var settings = new DbSettings
        {
            Integrations = new()
            {
                CompletionCommand = new()
                {
                    ExecutablePath = "/bin/echo",
                    TimeoutSeconds = 7
                }
            },
            Storage = new() { DownloadPath = baseDownloadPath }
        };
        var mocks = new Mocks();
        mocks.TorrentDataMock.Setup(t => t.GetById(torrent.TorrentId)).ReturnsAsync(torrent);
        mocks.DownloadsMock.Setup(d => d.GetForTorrent(torrent.TorrentId)).ReturnsAsync(downloads);

        var torrentPath = Path.Combine(baseDownloadPath, torrent.Category!, torrent.RdName!);
        var filePath = Path.Combine(torrentPath, downloads[0].FileName!);
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [filePath] = new("Test file")
        });
        var service = new TorrentsService(
            mocks.TorrentsLoggerMock.Object,
            mocks.TorrentDataMock.Object,
            mocks.DownloadsMock.Object,
            mocks.ProcessFactoryMock.Object,
            fileSystem,
            mocks.EnricherMock.Object,
            null!);
        mocks.ProcessMock.Setup(process => process.WaitForExit(7_000)).Returns(false);
        mocks.ProcessMock.Setup(process => process.WaitForExit(5_000)).Returns(true);

        await service.RunTorrentComplete(torrent.TorrentId, settings);

        mocks.ProcessMock.Verify(process => process.Kill(true), Times.Once);
        mocks.ProcessMock.Verify(process => process.WaitForExit(7_000), Times.Once);
        mocks.ProcessMock.Verify(process => process.WaitForExit(5_000), Times.Once);
    }

    [Theory]
    [MemberData(nameof(TorrentAndDownload))]
    public async Task RunTorrentComplete_WhenCommandNotSet_ShouldNotRunCommand(Torrent torrent, List<Download> downloads)
    {
        // Arrange
        var settings = new DbSettings
        {
            Integrations = new()
            {
                CompletionCommand = new() { ExecutablePath = null }
            }
        };

        var mocks = new Mocks();

        mocks.TorrentDataMock.Setup(t => t.GetById(torrent.TorrentId)).Returns(Task.FromResult<Torrent?>(torrent));
        mocks.DownloadsMock.Setup(d => d.GetForTorrent(torrent.TorrentId)).ReturnsAsync(downloads);

        var downloadPath = $"{settings.Storage.DownloadPath}/{torrent.Category}";
        var torrentPath = $"{downloadPath}/{torrent.RdName}";
        var filePath = $"{torrentPath}/{downloads[0].FileName}";

        var fileSystemMock = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            {
                filePath, new("Test file")
            },
        });

        var torrents = new TorrentsService(mocks.TorrentsLoggerMock.Object,
                                           mocks.TorrentDataMock.Object,
                                           mocks.DownloadsMock.Object,
                                           mocks.ProcessFactoryMock.Object,
                                           fileSystemMock,
                                           mocks.EnricherMock.Object,
                                           null!); // AllDebridTorrentClient not used by RunTorrentComplete

        //Act
        await torrents.RunTorrentComplete(torrent.TorrentId, settings);

        //Assert
        mocks.ProcessFactoryMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunTorrentComplete_WhenNoFilesWereDownloaded_ShouldNotRunCommand()
    {
        var settings = new DbSettings
        {
            Integrations = new()
            {
                CompletionCommand = new() { ExecutablePath = "/bin/echo" }
            }
        };
        var torrentId = Guid.NewGuid();
        var mocks = new Mocks();
        mocks.DownloadsMock.Setup(downloads => downloads.GetForTorrent(torrentId)).ReturnsAsync([]);
        var service = CreateService(mocks);

        await service.RunTorrentComplete(torrentId, settings);

        mocks.ProcessFactoryMock.VerifyNoOtherCalls();
        mocks.TorrentDataMock.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(TorrentAndDownload))]
    public async Task RunTorrentComplete_WhenStdOut_Logs(Torrent torrent, List<Download> downloads)
    {
        // Arrange
        var baseDownloadPath = Path.Combine(Path.GetTempPath(), "adb-test-downloads");
        var settings = new DbSettings
        {
            Integrations = new()
            {
                CompletionCommand = new() { ExecutablePath = "/bin/echo" }
            },
            Storage = new() { DownloadPath = baseDownloadPath }
        };

        var mocks = new Mocks();

        mocks.TorrentDataMock.Setup(t => t.GetById(torrent.TorrentId)).Returns(Task.FromResult<Torrent?>(torrent));
        mocks.DownloadsMock.Setup(d => d.GetForTorrent(torrent.TorrentId)).ReturnsAsync(downloads);

        var downloadPath = Path.Combine(baseDownloadPath, torrent.Category!);
        var torrentPath = Path.Combine(downloadPath, torrent.RdName!);
        var filePath = Path.Combine(torrentPath, downloads[0].FileName!);

        var fileSystemMock = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            {
                filePath, new("Test file")
            },
        });

        var torrents = new TorrentsService(mocks.TorrentsLoggerMock.Object,
                                           mocks.TorrentDataMock.Object,
                                           mocks.DownloadsMock.Object,
                                           mocks.ProcessFactoryMock.Object,
                                           fileSystemMock,
                                           mocks.EnricherMock.Object,
                                           null!); // AllDebridTorrentClient not used by RunTorrentComplete

        mocks.ProcessMock.Setup(p => p.WaitForExit(It.IsAny<int>()))
             .Callback(() =>
             {
                 mocks.ProcessMock.Raise(m => m.OutputDataReceived += null, this, "output-line 1");
                 mocks.ProcessMock.Raise(m => m.OutputDataReceived += null, this, "output-line 2");
                 mocks.ProcessMock.Raise(m => m.OutputDataReceived += null, this, "output-line 3");
             })
             .Returns(true);

        // Act
        await torrents.RunTorrentComplete(torrent.TorrentId, settings);

        // Assert
        mocks.ProcessMock.Verify(p => p.BeginOutputReadLine(), Times.Once);

        var messages = mocks.TorrentsLoggerMock.Invocations.Where(i => i.Method.Name == "Log").Select(i => i.Arguments[2].ToString()).Where(m => m != null).ToList();
        var exitedWithOutputMessages = messages.Where(m => Regex.IsMatch(m!, "exited with output")).ToList();
        Assert.NotNull(exitedWithOutputMessages);
        Assert.Single(exitedWithOutputMessages);
        var exitedWithOutputMessage = exitedWithOutputMessages.First();
        Assert.NotNull(exitedWithOutputMessage);
        Assert.Matches("output-line 1", exitedWithOutputMessage);
        Assert.Matches("output-line 2", exitedWithOutputMessage);
        Assert.Matches("output-line 3", exitedWithOutputMessage);
    }

    [Theory]
    [MemberData(nameof(TorrentAndDownload))]
    public async Task RunTorrentComplete_WhenStdErr_Logs(Torrent torrent, List<Download> downloads)
    {
        // Arrange
        var baseDownloadPath = Path.Combine(Path.GetTempPath(), "adb-test-downloads");
        var settings = new DbSettings
        {
            Integrations = new()
            {
                CompletionCommand = new() { ExecutablePath = "/bin/echo" }
            },
            Storage = new() { DownloadPath = baseDownloadPath }
        };

        var mocks = new Mocks();

        mocks.TorrentDataMock.Setup(t => t.GetById(torrent.TorrentId)).Returns(Task.FromResult<Torrent?>(torrent));
        mocks.DownloadsMock.Setup(d => d.GetForTorrent(torrent.TorrentId)).ReturnsAsync(downloads);

        var downloadPath = Path.Combine(baseDownloadPath, torrent.Category!);
        var torrentPath = Path.Combine(downloadPath, torrent.RdName!);
        var filePath = Path.Combine(torrentPath, downloads[0].FileName!);

        var fileSystemMock = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            {
                filePath, new("Test file")
            },
        });

        var torrents = new TorrentsService(mocks.TorrentsLoggerMock.Object,
                                           mocks.TorrentDataMock.Object,
                                           mocks.DownloadsMock.Object,
                                           mocks.ProcessFactoryMock.Object,
                                           fileSystemMock,
                                           mocks.EnricherMock.Object,
                                           null!); // AllDebridTorrentClient not used by RunTorrentComplete

        mocks.ProcessMock.Setup(p => p.WaitForExit(It.IsAny<int>()))
             .Callback(() =>
             {
                 mocks.ProcessMock.Raise(m => m.ErrorDataReceived += null, this, "error-line 1");
                 mocks.ProcessMock.Raise(m => m.ErrorDataReceived += null, this, "error-line 2");
                 mocks.ProcessMock.Raise(m => m.ErrorDataReceived += null, this, "error-line 3");
             })
             .Returns(true);

        // Act
        await torrents.RunTorrentComplete(torrent.TorrentId, settings);

        // Assert
        mocks.ProcessMock.Verify(p => p.BeginErrorReadLine(), Times.Once);

        var messages = mocks.TorrentsLoggerMock.Invocations.Where(i => i.Method.Name == "Log").Select(i => i.Arguments[2].ToString()).Where(m => m != null).ToList();
        var exitedWithOutputMessages = messages.Where(m => Regex.IsMatch(m!, "exited with errors")).ToList();
        Assert.NotNull(exitedWithOutputMessages);
        Assert.Single(exitedWithOutputMessages);
        var exitedWithOutputMessage = exitedWithOutputMessages.First();
        Assert.NotNull(exitedWithOutputMessage);
        Assert.Matches("error-line 1", exitedWithOutputMessage);
        Assert.Matches("error-line 2", exitedWithOutputMessage);
        Assert.Matches("error-line 3", exitedWithOutputMessage);
    }

    [Fact]
    public async Task Delete_WhenNoClientIsActive_DeletesImmediately()
    {
        var (torrent, _, mocks) = CreateTorrentForCancellationTest();
        var service = CreateService(mocks);

        await service.Delete(torrent.TorrentId, true, false, false);

        mocks.TorrentDataMock.Verify(data => data.Delete(torrent.TorrentId), Times.Once);
    }

    [Fact]
    public async Task Delete_WhenNoEffectIsRequested_DoesNotLoadOrMutateTorrent()
    {
        var mocks = new Mocks();
        var service = CreateService(mocks);

        await service.Delete(Guid.NewGuid(), false, false, false);

        mocks.TorrentDataMock.VerifyNoOtherCalls();
        mocks.DownloadsMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Delete_WhenDownloadIsActive_DetachesAndWaitsBeforeDeleting()
    {
        var (torrent, download, mocks) = CreateTorrentForCancellationTest();
        var downloader = new ControlledDownloader();
        var destinationPath = CreateTestDirectory();
        var client = new DownloadClient(
            download,
            torrent,
            destinationPath,
            (_, _) => downloader);
        var service = CreateService(mocks, TimeSpan.FromSeconds(2));

        try
        {
            await client.Start();
            TorrentRunner.ActiveDownloadClients[download.DownloadId] = client;

            var deletion = service.Delete(torrent.TorrentId, true, false, false);
            await downloader.CancelStarted.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.False(TorrentRunner.ActiveDownloadClients.ContainsKey(download.DownloadId));
            mocks.TorrentDataMock.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);

            downloader.ReleaseCancellation();
            await deletion;

            Assert.Equal(1, downloader.CancelCalls);
            mocks.TorrentDataMock.Verify(data => data.Delete(torrent.TorrentId), Times.Once);
        }
        finally
        {
            downloader.ReleaseCancellation();
            TorrentRunner.ActiveDownloadClients.TryRemove(download.DownloadId, out _);
            DeleteTestDirectory(destinationPath);
        }
    }

    [Fact]
    public async Task Delete_WhenCalledConcurrently_SerializesUntilActiveWorkStops()
    {
        var (torrent, download, mocks) = CreateTorrentForCancellationTest();
        mocks.TorrentDataMock
             .SetupSequence(data => data.GetById(torrent.TorrentId))
             .ReturnsAsync(torrent)
             .ReturnsAsync((Torrent?)null);
        var downloader = new ControlledDownloader();
        var destinationPath = CreateTestDirectory();
        var client = new DownloadClient(
            download,
            torrent,
            destinationPath,
            (_, _) => downloader);
        var service = CreateService(mocks, TimeSpan.FromSeconds(2));

        try
        {
            await client.Start();
            TorrentRunner.ActiveDownloadClients[download.DownloadId] = client;

            var firstDeletion = service.Delete(torrent.TorrentId, true, false, false);
            await downloader.CancelStarted.WaitAsync(TimeSpan.FromSeconds(1));

            var secondStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondDeletion = Task.Run(async () =>
            {
                secondStarted.TrySetResult();
                await service.Delete(torrent.TorrentId, true, false, false);
            });
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var earlyCompletion = await Task.WhenAny(
                secondDeletion,
                Task.Delay(TimeSpan.FromMilliseconds(100)));

            Assert.NotSame(secondDeletion, earlyCompletion);
            mocks.TorrentDataMock.Verify(
                data => data.GetById(torrent.TorrentId),
                Times.Once);
            mocks.TorrentDataMock.Verify(
                data => data.Delete(It.IsAny<Guid>()),
                Times.Never);

            downloader.ReleaseCancellation();
            await Task.WhenAll(firstDeletion, secondDeletion);

            Assert.Equal(1, downloader.CancelCalls);
            mocks.TorrentDataMock.Verify(
                data => data.GetById(torrent.TorrentId),
                Times.Exactly(2));
            mocks.TorrentDataMock.Verify(
                data => data.Delete(torrent.TorrentId),
                Times.Once);
        }
        finally
        {
            downloader.ReleaseCancellation();
            TorrentRunner.ActiveDownloadClients.TryRemove(download.DownloadId, out _);
            DeleteTestDirectory(destinationPath);
        }
    }

    [Fact]
    public async Task Delete_WhenDownloadDoesNotStop_AbortsAndRestoresOwnership()
    {
        var (torrent, download, mocks) = CreateTorrentForCancellationTest();
        var downloader = new ControlledDownloader();
        var destinationPath = CreateTestDirectory();
        var localRoot = Path.Combine(destinationPath, "downloads");
        var localFile = Path.Combine(localRoot, "radarr", torrent.RdName!, "file.bin");
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [localFile] = new("must remain")
        });
        torrent.LocalDownloadPath = localRoot;
        torrent.Category = "radarr";
        torrent.RdId = "provider-torrent";
        var client = new DownloadClient(
            download,
            torrent,
            destinationPath,
            (_, _) => downloader);
        var service = CreateService(mocks, TimeSpan.FromMilliseconds(25), fileSystem);

        try
        {
            await client.Start();
            TorrentRunner.ActiveDownloadClients[download.DownloadId] = client;

            var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
                service.Delete(torrent.TorrentId, true, true, true));

            Assert.Contains("No files or records were deleted", exception.Message);
            Assert.True(fileSystem.File.Exists(localFile));
            Assert.True(TorrentRunner.ActiveDownloadClients.TryGetValue(download.DownloadId, out var restored));
            Assert.Same(client, restored);
            Assert.Equal("The download cancellation could not be confirmed.", restored.Error);
            Assert.Equal(1, downloader.CancelCalls);
            mocks.TorrentDataMock.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);
            mocks.TorrentDataMock.Verify(
                data => data.FinalizeRetainedDeletion(
                    It.IsAny<Guid>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>()),
                Times.Never);
            mocks.AllDebridMagnetsMock.Verify(
                magnets => magnets.DeleteAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            downloader.ReleaseCancellation();
            await client.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(1));
            TorrentRunner.ActiveDownloadClients.TryRemove(download.DownloadId, out _);
            DeleteTestDirectory(destinationPath);
        }
    }

    [Fact]
    public async Task Delete_WhenUnpackIsActive_DetachesAndConfirmsCancellation()
    {
        var (torrent, download, mocks) = CreateTorrentForCancellationTest();
        var destinationPath = CreateTestDirectory();
        var workStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new UnpackClient(
            download,
            destinationPath,
            async (_, cancellationToken) =>
            {
                workStarted.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    await allowCompletion.Task;
                    throw;
                }
            });
        var service = CreateService(mocks, TimeSpan.FromSeconds(2));

        try
        {
            TorrentRunner.ActiveUnpackClients[download.DownloadId] = client;
            client.Start();
            await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var deletion = service.Delete(torrent.TorrentId, true, false, false);
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.False(TorrentRunner.ActiveUnpackClients.ContainsKey(download.DownloadId));
            mocks.TorrentDataMock.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);

            allowCompletion.TrySetResult();
            await deletion;

            Assert.True(client.Finished);
            Assert.Equal("The unpack was cancelled", client.Error);
            Assert.False(TorrentRunner.ActiveUnpackClients.ContainsKey(download.DownloadId));
            mocks.TorrentDataMock.Verify(data => data.Delete(torrent.TorrentId), Times.Once);
        }
        finally
        {
            client.Cancel();
            allowCompletion.TrySetResult();
            await client.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(1));
            TorrentRunner.ActiveUnpackClients.TryRemove(download.DownloadId, out _);
            DeleteTestDirectory(destinationPath);
        }
    }

    [Fact]
    public async Task Delete_WhenUnpackDoesNotStop_PreservesUnconfirmedErrorAfterCompletion()
    {
        var (torrent, download, mocks) = CreateTorrentForCancellationTest();
        var destinationPath = CreateTestDirectory();
        var workStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new UnpackClient(
            download,
            destinationPath,
            async (_, cancellationToken) =>
            {
                workStarted.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    await allowCompletion.Task;
                    throw;
                }
            });
        var service = CreateService(mocks, TimeSpan.FromMilliseconds(25));

        try
        {
            TorrentRunner.ActiveUnpackClients[download.DownloadId] = client;
            client.Start();
            await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            await Assert.ThrowsAsync<TimeoutException>(() =>
                service.Delete(torrent.TorrentId, true, false, false));
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(TorrentRunner.ActiveUnpackClients.TryGetValue(download.DownloadId, out var restored));
            Assert.Same(client, restored);
            Assert.Equal("The unpack cancellation could not be confirmed.", client.Error);
            mocks.TorrentDataMock.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);

            allowCompletion.TrySetResult();
            await client.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(client.Finished);
            Assert.Equal("The unpack cancellation could not be confirmed.", client.Error);
        }
        finally
        {
            client.Cancel();
            allowCompletion.TrySetResult();
            await client.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(1));
            TorrentRunner.ActiveUnpackClients.TryRemove(download.DownloadId, out _);
            DeleteTestDirectory(destinationPath);
        }
    }

    [Fact]
    public async Task Delete_WhenDownloadCancellationThrows_RestoresOwnershipAndCanRetry()
    {
        var (torrent, download, mocks) = CreateTorrentForCancellationTest();
        var downloader = new FailOnceDownloader();
        var destinationPath = CreateTestDirectory();
        var client = new DownloadClient(
            download,
            torrent,
            destinationPath,
            (_, _) => downloader);
        var service = CreateService(mocks, TimeSpan.FromSeconds(1));

        try
        {
            await client.Start();
            TorrentRunner.ActiveDownloadClients[download.DownloadId] = client;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.Delete(torrent.TorrentId, true, false, false));

            Assert.True(TorrentRunner.ActiveDownloadClients.TryGetValue(download.DownloadId, out var restored));
            Assert.Same(client, restored);
            Assert.Equal("The download cancellation could not be confirmed.", restored.Error);
            Assert.Equal(1, downloader.CancelCalls);
            mocks.TorrentDataMock.Verify(data => data.Delete(It.IsAny<Guid>()), Times.Never);

            await service.Delete(torrent.TorrentId, true, false, false);

            Assert.Equal(2, downloader.CancelCalls);
            Assert.False(TorrentRunner.ActiveDownloadClients.ContainsKey(download.DownloadId));
            mocks.TorrentDataMock.Verify(data => data.Delete(torrent.TorrentId), Times.Once);
        }
        finally
        {
            TorrentRunner.ActiveDownloadClients.TryRemove(download.DownloadId, out _);
            DeleteTestDirectory(destinationPath);
        }
    }

    [Fact]
    public void DownloadPath_UsesRootCapturedByTorrent()
    {
        var root = Path.Combine(Path.GetTempPath(), "adb-original-download-root");
        var torrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = Guid.NewGuid().ToString("N"),
            Category = "radarr",
            LocalDownloadPath = root
        };
        var service = CreateService(new Mocks());

        var result = service.DownloadPath(torrent);

        Assert.Equal(Path.Combine(root, "radarr"), result);
    }

    private static (Torrent Torrent, Download Download, Mocks Mocks) CreateTorrentForCancellationTest()
    {
        var torrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Hash = Guid.NewGuid().ToString("N"),
            RdName = "cancellation-test"
        };
        var download = new Download
        {
            DownloadId = Guid.NewGuid(),
            TorrentId = torrent.TorrentId,
            Torrent = torrent,
            Path = "file.bin",
            Link = "https://example.invalid/file.bin",
            FileName = "file.bin"
        };
        torrent.Downloads.Add(download);

        var mocks = new Mocks();
        mocks.TorrentDataMock.Setup(data => data.GetById(torrent.TorrentId)).ReturnsAsync(torrent);

        return (torrent, download, mocks);
    }

    private static Torrent CreateRetainedTorrent(bool completed)
    {
        return new()
        {
            TorrentId = Guid.NewGuid(),
            Hash = ExistingHash,
            Category = "radarr",
            LocalDownloadPath = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "adbclient-qbittorrent-reactivation")),
            RdId = "provider-id",
            RdName = "retained-job",
            RdStatus = TorrentStatus.Finished,
            FileOrMagnet = ExistingMagnet,
            Completed = completed ? DateTimeOffset.UtcNow : null,
            QbittorrentHidden = true
        };
    }

    private static Download AddExpectedDownload(Torrent torrent, string fileName)
    {
        var download = new Download
        {
            DownloadId = Guid.NewGuid(),
            TorrentId = torrent.TorrentId,
            Path = "https://example.invalid/restricted",
            FileName = fileName,
            Completed = DateTimeOffset.UtcNow
        };
        torrent.Downloads.Add(download);
        return download;
    }

    private static Mocks CreateMocksForExistingTorrent(Torrent torrent)
    {
        var mocks = new Mocks();
        mocks.EnricherMock.Setup(value => value.EnrichMagnetLink(ExistingMagnet))
             .ReturnsAsync(ExistingMagnet);
        mocks.TorrentDataMock.Setup(data => data.GetByHash(It.Is<string>(hash =>
                                     hash.Equals(ExistingHash, StringComparison.OrdinalIgnoreCase))))
             .ReturnsAsync(torrent);
        return mocks;
    }

    private static void SetupReactivation(Mocks mocks, Torrent torrent)
    {
        mocks.TorrentDataMock.Setup(data => data.ReactivateFromQbittorrent(
                                  torrent.TorrentId,
                                  It.IsAny<Torrent>(),
                                  It.IsAny<IReadOnlySet<Guid>?>(),
                                  It.IsAny<bool>()))
             .Callback(() => torrent.QbittorrentHidden = false)
             .ReturnsAsync(torrent);
    }

    private static TorrentsService CreateService(
        Mocks mocks,
        TimeSpan? activeClientStopTimeout = null,
        IFileSystem? fileSystem = null)
    {
        var torrentClient = new AllDebridTorrentClient(
            Mock.Of<ILogger<AllDebridTorrentClient>>(),
            mocks.AllDebridClientFactoryMock.Object,
            Mock.Of<IDownloadableFileFilter>());

        return new TorrentsService(
            mocks.TorrentsLoggerMock.Object,
            mocks.TorrentDataMock.Object,
            mocks.DownloadsMock.Object,
            mocks.ProcessFactoryMock.Object,
            fileSystem ?? new MockFileSystem(),
            mocks.EnricherMock.Object,
            torrentClient,
            activeClientStopTimeout);
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adbclient-active-work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTestDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private sealed class ControlledDownloader : IDownloader
    {
        private readonly TaskCompletionSource _allowCancellation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancelCalls;

        public event EventHandler<DownloadCompleteEventArgs>? DownloadComplete;
        public event EventHandler<DownloadProgressEventArgs>? DownloadProgress
        {
            add { }
            remove { }
        }

        public int CancelCalls => Volatile.Read(ref _cancelCalls);
        public Task CancelStarted => _cancelStarted.Task;

        public Task<string> Download()
        {
            return Task.FromResult("controlled-download");
        }

        public async Task Cancel()
        {
            Interlocked.Increment(ref _cancelCalls);
            _cancelStarted.TrySetResult();
            await _allowCancellation.Task;
            DownloadComplete?.Invoke(this, new() { Error = "The download was cancelled" });
        }

        public Task Pause()
        {
            return Task.CompletedTask;
        }

        public Task Resume()
        {
            return Task.CompletedTask;
        }

        public void ReleaseCancellation()
        {
            _allowCancellation.TrySetResult();
        }
    }

    private sealed class FailOnceDownloader : IDownloader
    {
        private int _cancelCalls;

        public event EventHandler<DownloadCompleteEventArgs>? DownloadComplete;
        public event EventHandler<DownloadProgressEventArgs>? DownloadProgress
        {
            add { }
            remove { }
        }

        public int CancelCalls => Volatile.Read(ref _cancelCalls);

        public Task<string> Download()
        {
            return Task.FromResult("fail-once-download");
        }

        public Task Cancel()
        {
            if (Interlocked.Increment(ref _cancelCalls) == 1)
            {
                throw new InvalidOperationException("Cancellation failed.");
            }

            DownloadComplete?.Invoke(this, new() { Error = "The download was cancelled" });
            return Task.CompletedTask;
        }

        public Task Pause()
        {
            return Task.CompletedTask;
        }

        public Task Resume()
        {
            return Task.CompletedTask;
        }
    }
}
