using System.Diagnostics;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using System.Text.RegularExpressions;
using AdbClient.Data.Data;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.Internal;
using AdbClient.Service.Services;
using AdbClient.Service.Wrappers;
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

    public Mocks()
    {
        TorrentDataMock = new();
        DownloadsMock = new();
        EnricherMock = new();

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
        var service = CreateService(mocks, _ => Task.CompletedTask);
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
    public async Task Delete_WhenNoClientIsActive_DoesNotPoll()
    {
        var (torrent, _, mocks) = CreateTorrentForCancellationTest();
        var delayCalls = 0;
        var service = CreateService(mocks, _ =>
        {
            delayCalls++;
            return Task.CompletedTask;
        });

        await service.Delete(torrent.TorrentId, true, false, false);

        Assert.Equal(0, delayCalls);
    }

    [Fact]
    public async Task Delete_WhenNoEffectIsRequested_DoesNotLoadOrMutateTorrent()
    {
        var mocks = new Mocks();
        var service = CreateService(mocks, _ => Task.CompletedTask);

        await service.Delete(Guid.NewGuid(), false, false, false);

        mocks.TorrentDataMock.VerifyNoOtherCalls();
        mocks.DownloadsMock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 10)]
    public async Task Delete_WhenClientRemainsActive_BoundsCancellationPolling(
        bool unpack,
        int expectedAttempts)
    {
        var (torrent, download, mocks) = CreateTorrentForCancellationTest();
        var delayCalls = 0;
        var service = CreateService(mocks, _ =>
        {
            delayCalls++;
            return Task.CompletedTask;
        });

        try
        {
            if (unpack)
            {
                TorrentRunner.ActiveUnpackClients[download.DownloadId] = new UnpackClient(download, "unused");
            }
            else
            {
                TorrentRunner.ActiveDownloadClients[download.DownloadId] = new DownloadClient(download, torrent, "unused");
            }

            await service.Delete(torrent.TorrentId, true, false, false);

            Assert.Equal(expectedAttempts, delayCalls);
        }
        finally
        {
            TorrentRunner.ActiveDownloadClients.TryRemove(download.DownloadId, out _);
            TorrentRunner.ActiveUnpackClients.TryRemove(download.DownloadId, out _);
        }
    }

    [Fact]
    public async Task Delete_WhenClientBecomesInactive_StopsPolling()
    {
        var (torrent, download, mocks) = CreateTorrentForCancellationTest();
        var delayCalls = 0;
        var service = CreateService(mocks, interval =>
        {
            delayCalls++;

            if (delayCalls == 2)
            {
                TorrentRunner.ActiveDownloadClients.TryRemove(download.DownloadId, out _);
            }

            return Task.CompletedTask;
        });

        try
        {
            TorrentRunner.ActiveDownloadClients[download.DownloadId] = new DownloadClient(download, torrent, "unused");

            await service.Delete(torrent.TorrentId, true, false, false);

            Assert.Equal(2, delayCalls);
        }
        finally
        {
            TorrentRunner.ActiveDownloadClients.TryRemove(download.DownloadId, out _);
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
        var service = CreateService(new Mocks(), _ => Task.CompletedTask);

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
            Path = "file.bin"
        };
        torrent.Downloads.Add(download);

        var mocks = new Mocks();
        mocks.TorrentDataMock.Setup(data => data.GetById(torrent.TorrentId)).ReturnsAsync(torrent);

        return (torrent, download, mocks);
    }

    private static TorrentsService CreateService(Mocks mocks, Func<TimeSpan, Task> delay)
    {
        return new TorrentsService(
            mocks.TorrentsLoggerMock.Object,
            mocks.TorrentDataMock.Object,
            mocks.DownloadsMock.Object,
            mocks.ProcessFactoryMock.Object,
            new MockFileSystem(),
            mocks.EnricherMock.Object,
            null!,
            delay);
    }
}
