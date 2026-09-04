using System.Net;
using AdbClient.Service.Services;
using AdbClient.Web.Controllers;
using AdbClient.Web.Models.Requests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace AdbClient.Service.Test.Controllers;

[Collection(SettingsIsolationCollection.Name)]
public class SettingsControllerTest
{
    [Fact]
    public async Task TestPath_PreservesExistingFilesAndRemovesItsTemporaryFile()
    {
        var directory = CreateTestDirectory();
        var existingFile = Path.Combine(directory, "test.txt");
        await File.WriteAllTextAsync(existingFile, "keep me");

        try
        {
            var controller = new TestableSettingsController("http://localhost", 1024);

            var result = await controller.TestPath(
                new SettingsControllerTestPathRequest { Path = directory },
                CancellationToken.None);

            Assert.IsType<OkResult>(result);
            Assert.Equal("keep me", await File.ReadAllTextAsync(existingFile));
            Assert.Equal([existingFile], Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TestWriteSpeed_PreservesExistingFilesAndRemovesItsTemporaryFile()
    {
        var directory = CreateTestDirectory();
        var existingFile = Path.Combine(directory, "test.tmp");
        await File.WriteAllTextAsync(existingFile, "keep me");
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;

        try
        {
            Settings.Get.Storage.DownloadPath = directory;
            var controller = new TestableSettingsController("http://localhost", 128 * 1024);

            var result = await controller.TestWriteSpeed(CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.True(Assert.IsType<double>(ok.Value) > 0);
            Assert.Equal("keep me", await File.ReadAllTextAsync(existingFile));
            Assert.Equal([existingFile], Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TestWriteSpeed_RemovesItsTemporaryFileWhenCancelled()
    {
        var directory = CreateTestDirectory();
        var existingFile = Path.Combine(directory, "test.tmp");
        await File.WriteAllTextAsync(existingFile, "keep me");
        var originalDownloadPath = Settings.Get.Storage.DownloadPath;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            Settings.Get.Storage.DownloadPath = directory;
            var controller = new TestableSettingsController("http://localhost", 128 * 1024);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => controller.TestWriteSpeed(cancellation.Token));

            Assert.Equal("keep me", await File.ReadAllTextAsync(existingFile));
            Assert.Equal([existingFile], Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Settings.Get.Storage.DownloadPath = originalDownloadPath;
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TestDownloadSpeed_WaitsForCompletionAndRemovesAllTestArtifacts()
    {
        var directory = CreateTestDirectory();
        var existingFile = Path.Combine(directory, "speed-test.bin");
        await File.WriteAllTextAsync(existingFile, "keep me");
        var originalSettings = CaptureDownloadSettings();
        await using var server = await LocalDownloadServer.Start(new byte[256 * 1024]);

        try
        {
            ApplyTestDownloadSettings(directory);
            var controller = new TestableSettingsController(server.Url, 1024);

            var action = controller.TestDownloadSpeed(CancellationToken.None);
            await server.BodyRequestStarted.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(action.IsCompleted);

            server.ReleaseBody();
            var result = await action.WaitAsync(TimeSpan.FromSeconds(30));

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.IsType<long>(ok.Value);
            Assert.Equal("keep me", await File.ReadAllTextAsync(existingFile));
            Assert.Equal([existingFile], Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            server.ReleaseBody();
            RestoreDownloadSettings(originalSettings);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TestDownloadSpeed_CancelsDownloaderAndRemovesAllTestArtifacts()
    {
        var directory = CreateTestDirectory();
        var existingFile = Path.Combine(directory, "speed-test.bin");
        await File.WriteAllTextAsync(existingFile, "keep me");
        var originalSettings = CaptureDownloadSettings();
        await using var server = await LocalDownloadServer.Start(new byte[256 * 1024]);
        using var cancellation = new CancellationTokenSource();

        try
        {
            ApplyTestDownloadSettings(directory);
            var controller = new TestableSettingsController(server.Url, 1024);

            var action = controller.TestDownloadSpeed(cancellation.Token);
            await server.BodyRequestStarted.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => action.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal("keep me", await File.ReadAllTextAsync(existingFile));
            Assert.Equal([existingFile], Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            server.ReleaseBody();
            RestoreDownloadSettings(originalSettings);
            Directory.Delete(directory, true);
        }
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"adbclient-settings-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static DownloadSettingsSnapshot CaptureDownloadSettings()
    {
        return new(
            Settings.Get.Storage.DownloadPath,
            Settings.Get.Downloads.ConnectionsPerFile,
            Settings.Get.Downloads.ChunksPerFile,
            Settings.Get.Downloads.SpeedLimit);
    }

    private static void ApplyTestDownloadSettings(string downloadPath)
    {
        Settings.Get.Storage.DownloadPath = downloadPath;
        Settings.Get.Downloads.ConnectionsPerFile = 1;
        Settings.Get.Downloads.ChunksPerFile = 1;
        Settings.Get.Downloads.SpeedLimit = 0;
    }

    private static void RestoreDownloadSettings(DownloadSettingsSnapshot settings)
    {
        Settings.Get.Storage.DownloadPath = settings.DownloadPath;
        Settings.Get.Downloads.ConnectionsPerFile = settings.ConnectionsPerFile;
        Settings.Get.Downloads.ChunksPerFile = settings.ChunksPerFile;
        Settings.Get.Downloads.SpeedLimit = settings.SpeedLimit;
    }

    private sealed record DownloadSettingsSnapshot(
        string DownloadPath,
        int ConnectionsPerFile,
        int ChunksPerFile,
        int SpeedLimit);

    private sealed class TestableSettingsController(string downloadSpeedTestUrl, int writeTestFileSize)
        : SettingsController(null!, null!, downloadSpeedTestUrl, writeTestFileSize);

    private sealed class LocalDownloadServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly TaskCompletionSource _bodyRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _bodyRequestStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] _payload;

        private LocalDownloadServer(WebApplication application, byte[] payload)
        {
            _application = application;
            _payload = payload;
        }

        public string Url { get; private set; } = null!;
        public Task BodyRequestStarted => _bodyRequestStarted.Task;

        public static async Task<LocalDownloadServer> Start(byte[] payload)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var application = builder.Build();
            var server = new LocalDownloadServer(application, payload);
            application.Run(server.HandleRequest);
            await application.StartAsync();

            var addresses = application.Services
                                       .GetRequiredService<IServer>()
                                       .Features
                                       .Get<IServerAddressesFeature>()!;
            server.Url = addresses.Addresses.Single();
            return server;
        }

        public void ReleaseBody()
        {
            _bodyRelease.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseBody();
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

            if (context.Request.Headers.ContainsKey("Range"))
            {
                context.Response.StatusCode = StatusCodes.Status206PartialContent;
                context.Response.Headers.ContentRange = $"bytes {start}-{end}/{_payload.Length}";
            }

            context.Response.ContentLength = length;
            _bodyRequestStarted.TrySetResult();
            await _bodyRelease.Task.WaitAsync(context.RequestAborted);
            await context.Response.Body.WriteAsync(
                _payload.AsMemory(start, length),
                context.RequestAborted);
        }

        private static (int Start, int End) ParseRange(string? header, int payloadLength)
        {
            if (string.IsNullOrWhiteSpace(header) ||
                !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
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
