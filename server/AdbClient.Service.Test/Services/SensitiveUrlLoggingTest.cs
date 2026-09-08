using System.ComponentModel;
using System.Net;
using AdbClient.Data.Models.Data;
using AdbClient.Service.Helpers;
using AdbClient.Service.Services;
using AdbClient.Service.Services.Downloaders;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AdbClient.Service.Test.Services;

[Collection(SettingsIsolationCollection.Name)]
public sealed class SensitiveUrlLoggingTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownloadLog_UsesSafeHostAndFileNameWithoutRestrictedUrlSecrets(bool hasUnrestrictedLink)
    {
        const string restrictedUrl =
            "https://restricted-user:restricted-password@restricted.example.test/private-passkey/episode.mkv?token=restricted-query#restricted-fragment";
        const string unrestrictedUrl =
            "https://link-user:link-password@link.example.test/another-passkey/episode.mkv?token=link-query#link-fragment";
        var download = new Download
        {
            Path = restrictedUrl,
            Link = hasUnrestrictedLink ? unrestrictedUrl : null,
            FileName = hasUnrestrictedLink ? "episode.mkv" : null,
            BytesDone = 25,
            BytesTotal = 100
        };

        var output = download.ToLog();

        Assert.Equal(hasUnrestrictedLink, output.Contains("download 'episode.mkv'", StringComparison.Ordinal));
        Assert.Contains(
            hasUnrestrictedLink ? "host 'link.example.test'" : "host 'restricted.example.test'",
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("-user", output, StringComparison.Ordinal);
        Assert.DoesNotContain("-password", output, StringComparison.Ordinal);
        Assert.DoesNotContain("passkey", output, StringComparison.Ordinal);
        Assert.DoesNotContain("-query", output, StringComparison.Ordinal);
        Assert.DoesNotContain("-fragment", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadLog_WithMalformedSource_DoesNotThrowOrEchoSource()
    {
        const string malformedSource = "https://bad host/private-passkey?token=query-secret\r\nfragment-secret";
        var download = new Download
        {
            Path = malformedSource,
            BytesDone = 0,
            BytesTotal = 0
        };

        var output = download.ToLog();

        Assert.Contains("download from an unknown source", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment-secret", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadLog_WithoutKnownFileName_DoesNotInferOneFromSignedPath()
    {
        var download = new Download
        {
            Link = "https://downloads.example.test/private-passkey/signed-secret.bin?token=query-secret"
        };

        var output = download.ToLog();

        Assert.Contains("download from host 'downloads.example.test'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InternalDownloader_LogsSafeDescriptorWithoutUrlSecrets()
    {
        const string sourceUrl =
            "https://download-user:download-password@downloads.example.test:8443/private-passkey/movie.mkv?token=query-secret#fragment-secret";
        var destination = Path.Combine(Path.GetTempPath(), $"adbclient-sensitive-url-{Guid.NewGuid():N}", "movie.mkv");
        var sink = new RecordingSink();
        var originalLogger = Serilog.Log.Logger;
        var testLogger = new LoggerConfiguration()
                         .MinimumLevel.Verbose()
                         .WriteTo.Sink(sink)
                         .CreateLogger();
        Serilog.Log.Logger = testLogger;

        try
        {
            var configuration = InternalDownloader.CreateDownloadConfiguration(new(), 1);
            var downloader = new InternalDownloader(sourceUrl, destination, configuration);

            await downloader.Cancel();

            var output = string.Join(Environment.NewLine, sink.Messages);
            Assert.Contains("download 'movie.mkv' from host 'downloads.example.test:8443'", output, StringComparison.Ordinal);
            Assert.DoesNotContain("download-user", output, StringComparison.Ordinal);
            Assert.DoesNotContain("download-password", output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
            Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("fragment-secret", output, StringComparison.Ordinal);
        }
        finally
        {
            Serilog.Log.Logger = originalLogger;
            testLogger.Dispose();
        }
    }

    [Fact]
    public async Task DownloadClientFailure_UsesSafeDescriptorWithoutUrlSecrets()
    {
        const string sourceUrl =
            "https://download-user:download-password@downloads.example.test/private-passkey/movie.mkv?token=query-secret#fragment-secret";
        var destination = Path.Combine(Path.GetTempPath(), $"adbclient-download-error-{Guid.NewGuid():N}");
        var torrent = new Torrent
        {
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdName = "Movie"
        };
        var download = new Download
        {
            Torrent = torrent,
            Path = sourceUrl,
            Link = sourceUrl,
            FileName = "movie.mkv"
        };
        var downloader = new SecretFailingDownloader(sourceUrl);
        var client = new DownloadClient(download, torrent, destination, (_, _) => downloader);

        try
        {
            var exception = await Assert.ThrowsAsync<Exception>(() => client.Start());
            var output = string.Join(Environment.NewLine, exception.ToString(), client.Error);

            Assert.Contains("download 'movie.mkv' from host 'downloads.example.test'", output, StringComparison.Ordinal);
            Assert.Contains("HttpRequestException (HTTP 502 BadGateway)", output, StringComparison.Ordinal);
            Assert.Contains(nameof(IOException), output, StringComparison.Ordinal);
            Assert.DoesNotContain("download-user", output, StringComparison.Ordinal);
            Assert.DoesNotContain("download-password", output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
            Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("fragment-secret", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, true);
            }
        }
    }

    [Fact]
    public async Task DownloadClientCompletion_PreservesFormattedDownloaderError()
    {
        const string sourceUrl =
            "https://download-user:download-password@downloads.example.test/private-passkey/movie.mkv?token=query-secret#fragment-secret";
        var destination = Path.Combine(Path.GetTempPath(), $"adbclient-download-completion-{Guid.NewGuid():N}");
        var torrent = new Torrent
        {
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdName = "Movie"
        };
        var download = new Download
        {
            Torrent = torrent,
            Path = sourceUrl,
            Link = sourceUrl,
            FileName = "movie.mkv"
        };
        var downloader = new FailedCompletionDownloader(sourceUrl);
        var client = new DownloadClient(download, torrent, destination, (_, _) => downloader);

        try
        {
            await client.Start();
            var completion = await client.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var output = string.Join(Environment.NewLine, client.Error, completion.Error);

            Assert.Contains("download 'movie.mkv' from host 'downloads.example.test'", output, StringComparison.Ordinal);
            Assert.Contains("HttpRequestException (HTTP 502 BadGateway)", output, StringComparison.Ordinal);
            Assert.Contains("HRESULT 0x", output, StringComparison.Ordinal);
            Assert.Equal(client.Error, completion.Error);
            Assert.DoesNotContain("download-user", output, StringComparison.Ordinal);
            Assert.DoesNotContain("download-password", output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
            Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("fragment-secret", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, true);
            }
        }
    }

    [Fact]
    public async Task InternalDownloaderCompletion_FormatsExceptionBeforeStringEventBoundary()
    {
        const string sourceUrl =
            "https://download-user:download-password@downloads.example.test/private-passkey/movie.mkv?token=query-secret#fragment-secret";
        var destination = Path.Combine(Path.GetTempPath(), $"adbclient-completion-{Guid.NewGuid():N}", "movie.mkv");
        var downloader = new InternalDownloader(
            sourceUrl,
            destination,
            InternalDownloader.CreateDownloadConfiguration(new(), 1));
        DownloadCompleteEventArgs? completion = null;
        downloader.DownloadComplete += (_, args) => completion = args;
        var exception = new HttpRequestException(
            $"{sourceUrl}\r\nUnrelated credential: xy",
            new IOException("Another secret absent from the source URL"),
            HttpStatusCode.BadGateway);

        try
        {
            downloader.OnDownloadFileCompleted(null, new AsyncCompletedEventArgs(exception, false, null));

            Assert.NotNull(completion);
            Assert.Equal(
                $"HttpRequestException (HTTP 502 BadGateway) (HRESULT 0x{exception.HResult:X8}) for download 'movie.mkv' from host 'downloads.example.test'.",
                completion.Error);
        }
        finally
        {
            await downloader.Cancel();
        }
    }

    [Fact]
    public async Task UnpackClientFailure_UsesSafeDescriptorWithoutUrlSecrets()
    {
        const string sourceUrl =
            "https://download-user:download-password@downloads.example.test/private-passkey/archive.zip?token=query-secret#fragment-secret";
        var destination = Path.Combine(Path.GetTempPath(), $"adbclient-unpack-error-{Guid.NewGuid():N}");
        var torrent = new Torrent
        {
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdName = "Archive"
        };
        var download = new Download
        {
            Torrent = torrent,
            Path = sourceUrl,
            Link = sourceUrl,
            FileName = "archive.zip"
        };
        var client = new UnpackClient(
            download,
            destination,
            (_, _) => throw new InvalidOperationException($"Archive setup failed for {sourceUrl}."));

        try
        {
            client.Start();
            await client.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var output = client.Error ?? string.Empty;

            Assert.Contains("download 'archive.zip' from host 'downloads.example.test'", output, StringComparison.Ordinal);
            Assert.Contains(nameof(InvalidOperationException), output, StringComparison.Ordinal);
            Assert.DoesNotContain("download-user", output, StringComparison.Ordinal);
            Assert.DoesNotContain("download-password", output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
            Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("fragment-secret", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, true);
            }
        }
    }

    [Fact]
    public void DownloadFailureDescription_UsesTypeStatusAndHResultWithoutExceptionMessage()
    {
        const string sourceUrl =
            "https://download-user:download-password@downloads.example.test:8443/private-passkey/movie.mkv?token=query-secret#fragment-secret";
        var exception = new HttpRequestException(
            $"Request failed for {sourceUrl}",
            null,
            HttpStatusCode.BadGateway);

        var output = AdbClient.Service.Helpers.Logger.DescribeDownloadFailure(
            exception,
            sourceUrl,
            "movie.mkv");

        Assert.Contains("HttpRequestException (HTTP 502 BadGateway)", output, StringComparison.Ordinal);
        Assert.Contains($"HRESULT 0x{exception.HResult:X8}", output, StringComparison.Ordinal);
        Assert.Contains("download 'movie.mkv' from host 'downloads.example.test:8443'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("download-user", output, StringComparison.Ordinal);
        Assert.DoesNotContain("download-password", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment-secret", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadFailureDescription_OmitsRawAndDecodedSecretsInExceptionMessage()
    {
        const string sourceUrl =
            "https://download-user:download%2Dpassword@downloads.example.test/private%2Dpasskey/movie.mkv?token=query%2Dsecret#fragment%2Dsecret";
        const string componentOnlyMessage =
            "download-user download%2Dpassword download-password private%2Dpasskey private-passkey token query%2Dsecret query-secret fragment%2Dsecret fragment-secret movie.mkv";
        var exception = new HttpRequestException(componentOnlyMessage);

        var output = AdbClient.Service.Helpers.Logger.DescribeDownloadFailure(
            exception,
            sourceUrl,
            "movie.mkv");

        Assert.Contains(nameof(HttpRequestException), output, StringComparison.Ordinal);
        Assert.Contains("movie.mkv", output, StringComparison.Ordinal);
        Assert.DoesNotContain("download-user", output, StringComparison.Ordinal);
        Assert.DoesNotContain("download%2Dpassword", output, StringComparison.Ordinal);
        Assert.DoesNotContain("download-password", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private%2Dpasskey", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
        Assert.DoesNotContain("token", output, StringComparison.Ordinal);
        Assert.DoesNotContain("query%2Dsecret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment%2Dsecret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment-secret", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadFailureDescription_OmitsExceptionMessageWithControlCharacters()
    {
        const string sourceUrl =
            "https://downloads.example.test/private-passkey/movie.mkv?token=query-secret";
        var exception = new IOException("Download failed\r\nfor query-secret\twith injected\0content.");

        var output = AdbClient.Service.Helpers.Logger.DescribeDownloadFailure(
            exception,
            sourceUrl,
            "movie.mkv");

        Assert.Contains(nameof(IOException), output, StringComparison.Ordinal);
        Assert.Contains($"HRESULT 0x{exception.HResult:X8}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Download failed", output, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
        Assert.All(output, character => Assert.False(char.IsControl(character)));
    }

    [Fact]
    public async Task QBittorrentMetadataFailure_LogsSafeOriginWithoutUrlSecrets()
    {
        const string torrentUrl =
            "https://metadata-user:metadata-password@metadata.example.test:8443/download/private-passkey/file.torrent?apiKey=query-secret#fragment-secret";
        var logger = new RecordingLogger<QBittorrentCompatibility>();
        var compatibility = new QBittorrentCompatibility(
            logger,
            null!,
            null!,
            null!,
            new SingleClientFactory(new ThrowingHandler(torrentUrl)),
            null!);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => compatibility.Add(torrentUrl, null));
        var output = string.Join(Environment.NewLine, logger.Messages.Append(exception.ToString()));

        Assert.Contains("https origin metadata.example.test on port 8443", output, StringComparison.Ordinal);
        Assert.Contains(nameof(HttpRequestException), output, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata-user", output, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata-password", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment-secret", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QBittorrentInvalidUrl_DoesNotEchoRawInput()
    {
        const string torrentUrl =
            "ftp://invalid-user:invalid-password@invalid.example.test/private-passkey/file.torrent?token=query-secret#fragment-secret";
        var logger = new RecordingLogger<QBittorrentCompatibility>();
        var compatibility = new QBittorrentCompatibility(logger, null!, null!, null!, null!, null!);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => compatibility.Add(torrentUrl, null));
        var output = string.Join(Environment.NewLine, logger.Messages.Append(exception.ToString()));

        Assert.Contains("Unsupported torrent URL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-user", output, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-password", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment-secret", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidTrackerListUrl_DoesNotEchoRawInput()
    {
        const string trackerListUrl =
            "invalid-url/private-passkey?apiKey=query-secret#fragment-secret";
        var originalUrl = Settings.Get.Provider.TrackerEnrichmentList;
        var logger = new RecordingLogger<TrackerListGrabber>();

        try
        {
            Settings.Get.Provider.TrackerEnrichmentList = trackerListUrl;
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var grabber = new TrackerListGrabber(null!, cache, logger);

            Assert.Empty(await grabber.GetTrackers());

            var output = string.Join(Environment.NewLine, logger.Messages);
            Assert.Contains("expected an absolute HTTP or HTTPS URL", output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
            Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("fragment-secret", output, StringComparison.Ordinal);
        }
        finally
        {
            Settings.Get.Provider.TrackerEnrichmentList = originalUrl;
        }
    }

    [Fact]
    public async Task TrackerListFetch_LogsSafeOriginWithoutSourceOrEntrySecrets()
    {
        const string trackerListUrl =
            "https://list-user:list-password@lists.example.test:9443/private-passkey/trackers.txt?apiKey=query-secret#fragment-secret";
        const string acceptedTracker =
            "https://tracker-user:tracker-password@tracker.example.test/announce/tracker-passkey?token=tracker-query#tracker-fragment";
        const string rejectedTracker =
            "invalid tracker/private-rejected-passkey?token=rejected-query#rejected-fragment";
        var originalUrl = Settings.Get.Provider.TrackerEnrichmentList;
        var originalExpiration = Settings.Get.Provider.TrackerEnrichmentCacheExpiration;
        var logger = new RecordingLogger<TrackerListGrabber>();

        try
        {
            Settings.Get.Provider.TrackerEnrichmentList = trackerListUrl;
            Settings.Get.Provider.TrackerEnrichmentCacheExpiration = 0;
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var content = string.Join('\n', acceptedTracker, rejectedTracker);
            var grabber = new TrackerListGrabber(
                new SingleClientFactory(new ContentHandler(content)),
                cache,
                logger);

            Assert.Equal([acceptedTracker], await grabber.GetTrackers());

            var output = string.Join(Environment.NewLine, logger.Messages);
            Assert.Contains("https origin lists.example.test on port 9443", output, StringComparison.Ordinal);
            Assert.Contains("Rejected tracker entry", output, StringComparison.Ordinal);
            Assert.DoesNotContain("list-user", output, StringComparison.Ordinal);
            Assert.DoesNotContain("list-password", output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
            Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("fragment-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-rejected-passkey", output, StringComparison.Ordinal);
            Assert.DoesNotContain("rejected-query", output, StringComparison.Ordinal);
            Assert.DoesNotContain("rejected-fragment", output, StringComparison.Ordinal);
            Assert.DoesNotContain("tracker-passkey", output, StringComparison.Ordinal);
            Assert.DoesNotContain("tracker-query", output, StringComparison.Ordinal);
            Assert.DoesNotContain("tracker-fragment", output, StringComparison.Ordinal);
        }
        finally
        {
            Settings.Get.Provider.TrackerEnrichmentList = originalUrl;
            Settings.Get.Provider.TrackerEnrichmentCacheExpiration = originalExpiration;
        }
    }

    [Fact]
    public async Task TrackerListFailure_DoesNotLogOrPropagateUrlSecrets()
    {
        const string trackerListUrl =
            "https://list-user:list-password@lists.example.test:9443/private-passkey/trackers.txt?apiKey=query-secret#fragment-secret";
        var originalUrl = Settings.Get.Provider.TrackerEnrichmentList;
        var originalExpiration = Settings.Get.Provider.TrackerEnrichmentCacheExpiration;
        var logger = new RecordingLogger<TrackerListGrabber>();

        try
        {
            Settings.Get.Provider.TrackerEnrichmentList = trackerListUrl;
            Settings.Get.Provider.TrackerEnrichmentCacheExpiration = 0;
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var grabber = new TrackerListGrabber(
                new SingleClientFactory(new ThrowingHandler(trackerListUrl)),
                cache,
                logger);

            var exception = await Assert.ThrowsAsync<Exception>(() => grabber.GetTrackers());
            var output = string.Join(Environment.NewLine, logger.Messages.Append(exception.ToString()));

            Assert.Contains("https origin lists.example.test on port 9443", output, StringComparison.Ordinal);
            Assert.Contains(nameof(HttpRequestException), output, StringComparison.Ordinal);
            Assert.DoesNotContain("list-user", output, StringComparison.Ordinal);
            Assert.DoesNotContain("list-password", output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-passkey", output, StringComparison.Ordinal);
            Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("fragment-secret", output, StringComparison.Ordinal);
        }
        finally
        {
            Settings.Get.Provider.TrackerEnrichmentList = originalUrl;
            Settings.Get.Provider.TrackerEnrichmentCacheExpiration = originalExpiration;
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);

        public HttpClient CreateClient(string name)
        {
            return _client;
        }
    }

    private sealed class ContentHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
        }
    }

    private sealed class ThrowingHandler(string sensitiveUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(new HttpRequestException(
                $"Request failed for {sensitiveUrl}",
                null,
                HttpStatusCode.BadGateway));
        }
    }

    private sealed class SecretFailingDownloader(string sourceUrl) : IDownloader
    {
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

        public Task<string> Download()
        {
            throw new HttpRequestException(
                $"Download attempt failed for {sourceUrl}",
                null,
                HttpStatusCode.BadGateway);
        }

        public Task Cancel()
        {
            throw new IOException($"Cleanup failed for {sourceUrl}");
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

    private sealed class FailedCompletionDownloader(string sourceUrl) : IDownloader
    {
        public event EventHandler<DownloadCompleteEventArgs>? DownloadComplete;

        public event EventHandler<DownloadProgressEventArgs>? DownloadProgress
        {
            add { }
            remove { }
        }

        public Task<string> Download()
        {
            DownloadComplete?.Invoke(this, new()
            {
                Error = AdbClient.Service.Helpers.Logger.DescribeDownloadFailure(
                    new HttpRequestException($"Request failed for {sourceUrl}", null, HttpStatusCode.BadGateway),
                    sourceUrl,
                    "movie.mkv")
            });
            return Task.FromResult("download-id");
        }

        public Task Cancel()
        {
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            Messages.Add(exception == null
                ? message
                : $"{message}{Environment.NewLine}{exception}");
        }
    }

    private sealed class RecordingSink : ILogEventSink
    {
        public List<string> Messages { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            var message = logEvent.RenderMessage();
            Messages.Add(logEvent.Exception == null
                ? message
                : $"{message}{Environment.NewLine}{logEvent.Exception}");
        }
    }
}
