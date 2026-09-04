using System.Diagnostics;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
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
using TorrentsService = AdbClient.Service.Services.Torrents;

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

    private static TorrentsService CreateService(
        Mocks mocks,
        TimeSpan? activeClientStopTimeout = null,
        MockFileSystem? fileSystem = null)
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
