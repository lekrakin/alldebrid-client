using AdbClient.Data.Models.Data;
using AdbClient.Service.Services;
using AdbClient.Service.Services.Downloaders;

namespace AdbClient.Service.Test.Services;

public class DownloadClientTest
{
    [Fact]
    public async Task CancelBeforeStart_CompletesAndPreventsLaterStart()
    {
        var torrent = new Torrent
        {
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdName = "cancel-before-start"
        };
        var download = new Download
        {
            Torrent = torrent,
            Link = "https://example.invalid/file.bin",
            FileName = "file.bin"
        };
        var factoryCalls = 0;
        var client = new DownloadClient(
            download,
            torrent,
            "unused",
            (_, _) =>
            {
                factoryCalls++;
                throw new InvalidOperationException("The downloader factory must not run.");
            });

        await client.Cancel();
        await client.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(client.Finished);
        Assert.Equal("The download was cancelled", client.Error);
        Assert.Equal(0, factoryCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Start());
    }

    [Fact]
    public async Task Start_WhenDownloadAndCleanupFail_CompletesWithOriginalError()
    {
        var torrent = new Torrent
        {
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdName = "failed-start"
        };
        var download = new Download
        {
            Torrent = torrent,
            Link = "https://example.invalid/file.bin",
            FileName = "file.bin"
        };
        var downloader = new FailingDownloader();
        var destinationPath = Path.Combine(
            Path.GetTempPath(),
            $"adbclient-download-client-{Guid.NewGuid():N}");
        var client = new DownloadClient(
            download,
            torrent,
            destinationPath,
            (_, _) => downloader);

        try
        {
            var exception = await Assert.ThrowsAsync<Exception>(() => client.Start());
            await client.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Contains("Download start failed.", exception.Message);
            var aggregate = Assert.IsType<AggregateException>(exception.InnerException);
            Assert.Contains(aggregate.InnerExceptions, error => error.Message == "Download start failed.");
            Assert.Contains(aggregate.InnerExceptions, error => error.Message == "Download cleanup failed.");
            Assert.True(client.Finished);
            Assert.Equal("Download start failed.", client.Error);
            Assert.Equal(1, downloader.CancelCalls);
        }
        finally
        {
            if (Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, true);
            }
        }
    }

    private sealed class FailingDownloader : IDownloader
    {
        private int _cancelCalls;

        public event EventHandler<DownloadCompleteEventArgs>? DownloadComplete
        {
            add { }
            remove { }
        }

        public event EventHandler<DownloadProgressEventArgs>? DownloadProgress
        {
            add { }
            remove { }
        }

        public int CancelCalls => Volatile.Read(ref _cancelCalls);

        public Task<string> Download()
        {
            throw new InvalidOperationException("Download start failed.");
        }

        public Task Cancel()
        {
            Interlocked.Increment(ref _cancelCalls);
            throw new IOException("Download cleanup failed.");
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
