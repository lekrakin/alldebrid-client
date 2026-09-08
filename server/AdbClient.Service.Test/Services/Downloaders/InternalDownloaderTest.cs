using System.Net;
using AdbClient.Data.Models.Internal;
using AdbClient.Service.Services.Downloaders;
using Downloader;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AdbClient.Service.Test.Services.Downloaders;

public class InternalDownloaderTest
{
    [Fact]
    public void Configuration_IsBoundedAndMapsDownloadSettings()
    {
        var settings = new DbSettingsDownloads
        {
            SpeedLimit = 96,
            ConnectionsPerFile = 4,
            ChunksPerFile = 12
        };

        var configuration = InternalDownloader.CreateDownloadConfiguration(settings, 3);

        Assert.Equal(10L * 1024 * 1024, configuration.MaximumMemoryBufferBytes);
        Assert.Equal(64 * 1024, configuration.BufferBlockSize);
        Assert.Equal(5000, configuration.BlockTimeout);
        Assert.Equal(30000, configuration.HttpClientTimeout);
        Assert.Equal(32L * 1024 * 1024, configuration.MaximumBytesPerSecond);
        Assert.Equal(5, configuration.MaxTryAgainOnFailure);
        Assert.Equal(4, configuration.ParallelCount);
        Assert.True(configuration.ParallelDownload);
        Assert.Equal(12, configuration.ChunkCount);
        Assert.True(configuration.ClearPackageOnCompletionWithFailure);
        Assert.False(configuration.CheckDiskSizeBeforeDownload);
        Assert.Equal(HttpVersion.Version11, configuration.RequestConfiguration.ProtocolVersion);
        Assert.Equal("alldebrid-client", configuration.RequestConfiguration.UserAgent);
        Assert.Null(typeof(DbSettingsDownloads).GetProperty("ChunkCount"));
        Assert.Null(typeof(DbSettingsDownloads).GetProperty("BufferSize"));
        Assert.Null(typeof(DbSettingsDownloads).GetProperty("LogLevel"));
        Assert.Null(typeof(DbSettingsDownloads).Assembly.GetType("AdbClient.Data.Enums.DownloadClientLogLevel"));
    }

    [Fact]
    public void Configuration_UsesSafeFallbacksAndCanBeUpdated()
    {
        var settings = new DbSettingsDownloads
        {
            SpeedLimit = 0,
            ConnectionsPerFile = 0,
            ChunksPerFile = 0
        };

        var configuration = InternalDownloader.CreateDownloadConfiguration(settings, 0);

        Assert.Equal(long.MaxValue, configuration.MaximumBytesPerSecond);
        Assert.Equal(1, configuration.ParallelCount);
        Assert.False(configuration.ParallelDownload);
        Assert.Equal(8, configuration.ChunkCount);

        InternalDownloader.ApplySpeedLimit(configuration, 80, 4);

        Assert.Equal(20L * 1024 * 1024, configuration.MaximumBytesPerSecond);
        Assert.Equal(1, configuration.ParallelCount);
        Assert.False(configuration.ParallelDownload);
        Assert.Equal(8, configuration.ChunkCount);

        InternalDownloader.ApplySpeedLimit(configuration, 80, 0);

        Assert.Equal(80L * 1024 * 1024, configuration.MaximumBytesPerSecond);

        settings.ConnectionsPerFile = int.MaxValue;
        settings.ChunksPerFile = int.MaxValue;
        configuration = InternalDownloader.CreateDownloadConfiguration(settings, 1);

        Assert.Equal(InternalDownloader.MaximumParallelConnections, configuration.ParallelCount);
        Assert.Equal(InternalDownloader.MaximumChunkCount, configuration.ChunkCount);

        settings.ConnectionsPerFile = 16;
        settings.ChunksPerFile = 4;
        configuration = InternalDownloader.CreateDownloadConfiguration(settings, 1);

        Assert.Equal(4, configuration.ParallelCount);
        Assert.Equal(4, configuration.ChunkCount);
    }

    [Fact]
    public async Task Download_WritesExactBytesAndSignalsSuccessOnce()
    {
        var payload = Enumerable.Range(0, 256 * 1024)
                                .Select(index => (byte)(index % 251))
                                .ToArray();
        await using var server = await LocalDownloadServer.Start(payload);
        var directory = CreateTestDirectory();
        var filePath = Path.Combine(directory, "completed.bin");

        try
        {
            var configuration = InternalDownloader.CreateDownloadConfiguration(
                new()
                {
                    SpeedLimit = 0,
                    ConnectionsPerFile = 3,
                    ChunksPerFile = 4
                },
                1);
            var downloader = new InternalDownloader(server.Url, filePath, configuration);
            var terminal = new TaskCompletionSource<DownloadCompleteEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var terminalCount = 0;
            var progressCount = 0;
            long largestProgress = 0;

            downloader.DownloadComplete += (_, args) =>
            {
                Interlocked.Increment(ref terminalCount);
                terminal.TrySetResult(args);
            };
            downloader.DownloadProgress += (_, args) =>
            {
                Interlocked.Increment(ref progressCount);
                InterlockedExtensions.Max(ref largestProgress, args.BytesDone);
            };

            var downloadId = await downloader.Download();
            var completion = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await downloader.Cancel();

            Assert.False(string.IsNullOrWhiteSpace(downloadId));
            Assert.Null(completion.Error);
            Assert.Equal(1, Volatile.Read(ref terminalCount));
            Assert.True(Volatile.Read(ref progressCount) > 0);
            Assert.Equal(payload.Length, Volatile.Read(ref largestProgress));
            Assert.Equal(payload, await File.ReadAllBytesAsync(filePath));
            Assert.False(File.Exists(filePath + configuration.DownloadFileExtension));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task Cancel_SignalsOnceRemovesTemporaryFileAndReleasesIt()
    {
        var payload = Enumerable.Repeat((byte)0x5a, 2 * 1024 * 1024).ToArray();
        await using var server = await LocalDownloadServer.Start(payload, slow: true);
        var directory = CreateTestDirectory();
        var filePath = Path.Combine(directory, "cancelled.bin");

        try
        {
            var configuration = InternalDownloader.CreateDownloadConfiguration(
                new()
                {
                    ConnectionsPerFile = 1,
                    ChunksPerFile = 1
                },
                1);
            var downloader = new InternalDownloader(server.Url, filePath, configuration);
            var terminal = new TaskCompletionSource<DownloadCompleteEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var terminalCount = 0;

            downloader.DownloadComplete += (_, args) =>
            {
                Interlocked.Increment(ref terminalCount);
                terminal.TrySetResult(args);
            };

            await downloader.Download();
            await server.BodyRequestStarted.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForFile(filePath + configuration.DownloadFileExtension);
            await downloader.Cancel();
            var completion = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await downloader.Cancel();

            Assert.Equal("The download was cancelled", completion.Error);
            Assert.Equal(1, Volatile.Read(ref terminalCount));
            Assert.False(File.Exists(filePath));
            Assert.False(File.Exists(filePath + configuration.DownloadFileExtension));

            await File.WriteAllBytesAsync(filePath + configuration.DownloadFileExtension, [1, 2, 3]);
            File.Delete(filePath + configuration.DownloadFileExtension);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task ConcurrentStartAndCancel_AreAtomicAndReleaseResources()
    {
        var payload = Enumerable.Repeat((byte)0x2a, 512 * 1024).ToArray();
        await using var server = await LocalDownloadServer.Start(payload, slow: true);
        var directory = CreateTestDirectory();

        try
        {
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var filePath = Path.Combine(directory, $"race-{iteration}.bin");
                var configuration = InternalDownloader.CreateDownloadConfiguration(
                    new()
                    {
                        ConnectionsPerFile = 1,
                        ChunksPerFile = 1
                    },
                    1);
                var downloader = new InternalDownloader(server.Url, filePath, configuration);
                var terminal = new TaskCompletionSource<DownloadCompleteEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var terminalCount = 0;

                downloader.DownloadComplete += (_, args) =>
                {
                    Interlocked.Increment(ref terminalCount);
                    terminal.TrySetResult(args);
                };

                var start = Task.Run(async () => await downloader.Download());
                var cancel = Task.Run(async () => await downloader.Cancel());

                await Task.WhenAll(start, cancel).WaitAsync(TimeSpan.FromSeconds(10));
                var completion = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await downloader.Cancel();

                Assert.Equal("The download was cancelled", completion.Error);
                Assert.Equal(1, Volatile.Read(ref terminalCount));
                Assert.False(File.Exists(filePath));
                Assert.False(File.Exists(filePath + configuration.DownloadFileExtension));
            }
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task PauseAndResume_PreserveTheActiveDownload()
    {
        var payload = Enumerable.Repeat((byte)0xa5, 1024 * 1024).ToArray();
        await using var server = await LocalDownloadServer.Start(payload, slow: true);
        var directory = CreateTestDirectory();
        var filePath = Path.Combine(directory, "paused.bin");

        try
        {
            var configuration = InternalDownloader.CreateDownloadConfiguration(
                new()
                {
                    ConnectionsPerFile = 1,
                    ChunksPerFile = 1
                },
                1);
            var downloader = new InternalDownloader(server.Url, filePath, configuration);
            var terminal = new TaskCompletionSource<DownloadCompleteEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var terminalCount = 0;
            var progressStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            long bytesDone = 0;

            downloader.DownloadComplete += (_, args) =>
            {
                Interlocked.Increment(ref terminalCount);
                terminal.TrySetResult(args);
            };
            downloader.DownloadProgress += (_, args) =>
            {
                Interlocked.Exchange(ref bytesDone, args.BytesDone);

                if (args.BytesDone > 0)
                {
                    progressStarted.TrySetResult();
                }
            };

            await downloader.Download();
            await server.BodyRequestStarted.WaitAsync(TimeSpan.FromSeconds(10));
            await progressStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await downloader.Pause();
            await Task.Delay(250);
            var pausedBytes = Volatile.Read(ref bytesDone);
            await Task.Delay(300);

            Assert.False(terminal.Task.IsCompleted);
            Assert.Equal(pausedBytes, Volatile.Read(ref bytesDone));

            await downloader.Resume();

            var completion = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await downloader.Cancel();

            Assert.Null(completion.Error);
            Assert.Equal(1, Volatile.Read(ref terminalCount));
            Assert.Equal(payload, await File.ReadAllBytesAsync(filePath));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task Failure_SignalsOnceAndLeavesNoTemporaryFile()
    {
        var payload = Enumerable.Repeat((byte)0x3c, 512 * 1024).ToArray();
        await using var server = await LocalDownloadServer.Start(payload, failDuringBody: true);
        var directory = CreateTestDirectory();
        var filePath = Path.Combine(directory, "failed.bin");

        try
        {
            var configuration = InternalDownloader.CreateDownloadConfiguration(
                new()
                {
                    ConnectionsPerFile = 1,
                    ChunksPerFile = 1
                },
                1);
            configuration.MaxTryAgainOnFailure = 0;
            var downloader = new InternalDownloader(server.Url, filePath, configuration);
            var terminal = new TaskCompletionSource<DownloadCompleteEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var terminalCount = 0;

            downloader.DownloadComplete += (_, args) =>
            {
                Interlocked.Increment(ref terminalCount);
                terminal.TrySetResult(args);
            };

            await downloader.Download();
            await server.BodyRequestStarted.WaitAsync(TimeSpan.FromSeconds(10));
            var completion = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await downloader.Cancel();

            Assert.False(string.IsNullOrWhiteSpace(completion.Error));
            Assert.Equal(1, Volatile.Read(ref terminalCount));
            Assert.False(File.Exists(filePath));
            Assert.False(File.Exists(filePath + configuration.DownloadFileExtension));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"adbclient-internal-downloader-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task WaitForFile(string filePath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (!File.Exists(filePath))
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref long location, long value)
        {
            var current = Volatile.Read(ref location);

            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);

                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class LocalDownloadServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly bool _failDuringBody;
        private readonly byte[] _payload;
        private readonly bool _slow;
        private readonly TaskCompletionSource _bodyRequestStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private LocalDownloadServer(
            WebApplication application,
            byte[] payload,
            bool slow,
            bool failDuringBody)
        {
            _application = application;
            _payload = payload;
            _slow = slow;
            _failDuringBody = failDuringBody;
        }

        public string Url { get; private set; } = null!;
        public Task BodyRequestStarted => _bodyRequestStarted.Task;

        public static async Task<LocalDownloadServer> Start(
            byte[] payload,
            bool slow = false,
            bool failDuringBody = false)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var application = builder.Build();
            var server = new LocalDownloadServer(application, payload, slow, failDuringBody);
            application.Run(server.HandleRequest);
            await application.StartAsync();

            var addresses = application.Services
                                       .GetRequiredService<IServer>()
                                       .Features
                                       .Get<IServerAddressesFeature>()!;
            server.Url = addresses.Addresses.Single();
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }

        private async Task HandleRequest(HttpContext context)
        {
            context.Response.Headers.AcceptRanges = "bytes";

            if (HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.ContentLength = _payload.Length;
                return;
            }

            var (start, end) = ParseRange(context.Request.Headers.Range, _payload.Length);
            var length = end - start + 1;

            if (length > 1)
            {
                _bodyRequestStarted.TrySetResult();
            }

            if (context.Request.Headers.ContainsKey("Range"))
            {
                context.Response.StatusCode = StatusCodes.Status206PartialContent;
                context.Response.Headers.ContentRange = $"bytes {start}-{end}/{_payload.Length}";
            }

            context.Response.ContentLength = length;

            const int blockSize = 8192;

            if (_failDuringBody && length > 1)
            {
                await context.Response.Body.WriteAsync(
                    _payload.AsMemory(start, Math.Min(blockSize, length)),
                    context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                context.Abort();
                return;
            }

            var offset = start;

            while (offset <= end)
            {
                var count = Math.Min(blockSize, end - offset + 1);
                await context.Response.Body.WriteAsync(
                    _payload.AsMemory(offset, count),
                    context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                offset += count;

                if (_slow)
                {
                    await Task.Delay(20, context.RequestAborted);
                }
            }
        }

        private static (int Start, int End) ParseRange(string? header, int payloadLength)
        {
            if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return (0, payloadLength - 1);
            }

            var parts = header[6..].Split('-', 2);
            var start = int.Parse(parts[0]);
            var end = string.IsNullOrWhiteSpace(parts[1])
                ? payloadLength - 1
                : Math.Min(int.Parse(parts[1]), payloadLength - 1);
            return (start, end);
        }
    }
}
