using System.ComponentModel;
using System.Net;
using AdbClient.Data.Models.Internal;
using AdbClient.Service.Helpers;
using Downloader;
using Serilog;

namespace AdbClient.Service.Services.Downloaders;

public class InternalDownloader : IDownloader
{
    internal const long MaximumMemoryBufferBytes = 10L * 1024 * 1024;
    internal const int MaximumParallelConnections = 16;
    internal const int MaximumChunkCount = 128;

    private const int BufferBlockSizeBytes = 64 * 1024;
    private const int DefaultChunkCount = 8;
    private const int BlockTimeoutMilliseconds = 5000;
    private const int HttpClientTimeoutMilliseconds = 30000;
    private const int LifecycleReady = 0;
    private const int LifecycleRunning = 1;
    private const int LifecycleCancelledBeforeStart = 2;

    private readonly CancellationTokenSource _downloadCancellation = new();
    private readonly Lock _downloadCancellationLock = new();
    private readonly DownloadConfiguration _downloadConfiguration;
    private readonly string? _downloadFileName;
    private readonly string _downloadId = Guid.NewGuid().ToString();
    private readonly DownloadService _downloadService;
    private readonly string _downloadSource;
    private readonly string _filePath;
    private readonly Lock _lifecycleLock = new();
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _settingsCancellation = new();
    private readonly bool _updateDynamicSettings;
    private readonly string _uri;

    private Task? _downloadTask;
    private Task? _settingsTask;
    private int _disposed;
    private int _lifecycleState;
    private int _terminalSignaled;
    private int _terminalSucceeded;

    public InternalDownloader(string uri, string filePath)
        : this(
            uri,
            filePath,
            CreateDownloadConfiguration(
                Settings.Get.Downloads,
                TorrentRunner.ActiveDownloadClients.Count),
            true)
    {
    }

    internal InternalDownloader(
        string uri,
        string filePath,
        DownloadConfiguration downloadConfiguration)
        : this(uri, filePath, downloadConfiguration, false)
    {
    }

    private InternalDownloader(
        string uri,
        string filePath,
        DownloadConfiguration downloadConfiguration,
        bool updateDynamicSettings)
    {
        _logger = Log.ForContext<InternalDownloader>();
        _downloadFileName = Path.GetFileName(filePath);
        _downloadSource = Logger.DescribeDownloadSource(uri, _downloadFileName);
        _logger.Debug(
            "Instantiated new Internal Downloader for {DownloadSource} to filePath {FilePath}",
            _downloadSource,
            filePath);

        _uri = uri;
        _filePath = filePath;
        _downloadConfiguration = downloadConfiguration;
        _updateDynamicSettings = updateDynamicSettings;
        _downloadService = new(_downloadConfiguration);

        _downloadService.DownloadProgressChanged += OnDownloadProgressChanged;
        _downloadService.DownloadFileCompleted += OnDownloadFileCompleted;
    }

    public event EventHandler<DownloadCompleteEventArgs>? DownloadComplete;
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgress;

    public Task<string> Download()
    {
        lock (_lifecycleLock)
        {
            if (_lifecycleState != LifecycleReady || Volatile.Read(ref _terminalSignaled) != 0)
            {
                return Task.FromResult(_downloadId);
            }

            _lifecycleState = LifecycleRunning;
            _logger.Debug("Starting {DownloadSource}, writing to path: {FilePath}", _downloadSource, _filePath);

            _settingsTask = _updateDynamicSettings ? StartSettingsTimer() : Task.CompletedTask;
            _downloadTask = RunDownloadAsync();
        }

        return Task.FromResult(_downloadId);
    }

    public async Task Cancel()
    {
        _logger.Debug("Cancelling {DownloadSource}", _downloadSource);

        Task? downloadTask;
        var cancelBeforeStart = false;
        var cancelRunning = false;

        lock (_lifecycleLock)
        {
            downloadTask = _downloadTask;

            if (Volatile.Read(ref _terminalSignaled) == 0)
            {
                if (_lifecycleState == LifecycleReady)
                {
                    _lifecycleState = LifecycleCancelledBeforeStart;
                    cancelBeforeStart = true;
                }
                else if (_lifecycleState == LifecycleRunning)
                {
                    cancelRunning = true;
                }
            }
        }

        if (cancelBeforeStart)
        {
            CancelDownloadSource();
            CompleteOnce("The download was cancelled");
            await DisposeTerminalResourcesAsync().ConfigureAwait(false);
            return;
        }

        if (!cancelRunning)
        {
            if (downloadTask != null)
            {
                await downloadTask.ConfigureAwait(false);
            }

            return;
        }

        CancelDownloadSource();

        try
        {
            await _downloadService.CancelTaskAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _terminalSignaled) != 0)
        {
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "Unable to wait for downloader cancellation for {DownloadSource} ({ExceptionType})",
                _downloadSource,
                ex.GetType().Name);
        }

        if (downloadTask != null)
        {
            await downloadTask.ConfigureAwait(false);
        }
    }

    public Task Pause()
    {
        if (Volatile.Read(ref _lifecycleState) == LifecycleRunning && Volatile.Read(ref _terminalSignaled) == 0)
        {
            _logger.Debug("Pausing {DownloadSource}", _downloadSource);
            _downloadService.Pause();
        }

        return Task.CompletedTask;
    }

    public Task Resume()
    {
        if (Volatile.Read(ref _lifecycleState) == LifecycleRunning && Volatile.Read(ref _terminalSignaled) == 0)
        {
            _logger.Debug("Resuming {DownloadSource}", _downloadSource);
            _downloadService.Resume();
        }

        return Task.CompletedTask;
    }

    internal static DownloadConfiguration CreateDownloadConfiguration(
        DbSettingsDownloads settings,
        int activeDownloadCount)
    {
        var chunkCount = Math.Clamp(
            settings.ChunksPerFile > 0 ? settings.ChunksPerFile : DefaultChunkCount,
            1,
            MaximumChunkCount);
        var parallelCount = Math.Min(
            Math.Clamp(settings.ConnectionsPerFile, 1, MaximumParallelConnections),
            chunkCount);
        var configuration = new DownloadConfiguration
        {
            MaxTryAgainOnFailure = 5,
            RangeDownload = false,
            ClearPackageOnCompletionWithFailure = true,
            CheckDiskSizeBeforeDownload = false,
            MaximumMemoryBufferBytes = MaximumMemoryBufferBytes,
            BufferBlockSize = BufferBlockSizeBytes,
            BlockTimeout = BlockTimeoutMilliseconds,
            HttpClientTimeout = HttpClientTimeoutMilliseconds,
            ParallelDownload = parallelCount > 1,
            ParallelCount = parallelCount,
            ChunkCount = chunkCount,
            RequestConfiguration =
            {
                Accept = "*/*",
                UserAgent = "alldebrid-client",
                ProtocolVersion = HttpVersion.Version11,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                KeepAlive = true,
                UseDefaultCredentials = false
            }
        };

        ApplySpeedLimit(configuration, settings.SpeedLimit, activeDownloadCount);

        return configuration;
    }

    internal static void ApplySpeedLimit(
        DownloadConfiguration configuration,
        int maximumMegabytesPerSecond,
        int activeDownloadCount)
    {
        var maximumBytesPerSecond = Math.Max(maximumMegabytesPerSecond, 0) * 1024L * 1024L;

        if (maximumBytesPerSecond > 0)
        {
            maximumBytesPerSecond /= Math.Max(activeDownloadCount, 1);
        }

        configuration.MaximumBytesPerSecond = maximumBytesPerSecond;
    }

    private void OnDownloadProgressChanged(object? sender, Downloader.DownloadProgressChangedEventArgs args)
    {
        DownloadProgress?.Invoke(this,
                                 new()
                                 {
                                     Speed = (long)args.BytesPerSecondSpeed,
                                     BytesDone = args.ReceivedBytesSize,
                                     BytesTotal = args.TotalBytesToReceive
                                 });
    }

    internal void OnDownloadFileCompleted(object? sender, AsyncCompletedEventArgs args)
    {
        string? error = null;

        if (args.Cancelled)
        {
            error = "The download was cancelled";
        }
        else if (args.Error != null)
        {
            error = Logger.DescribeDownloadFailure(args.Error, _uri, _downloadFileName);
        }

        CompleteOnce(error);
    }

    private async Task RunDownloadAsync()
    {
        try
        {
            await _downloadService.DownloadFileTaskAsync(
                _uri,
                _filePath,
                _downloadCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_downloadCancellation.IsCancellationRequested)
        {
            CompleteOnce("The download was cancelled");
        }
        catch (Exception ex)
        {
            CompleteOnce(Logger.DescribeDownloadFailure(ex, _uri, _downloadFileName));
        }
        finally
        {
            if (Volatile.Read(ref _terminalSignaled) == 0)
            {
                var error = _downloadService.Status == DownloadStatus.Completed
                    ? null
                    : $"The download ended without a completion signal (status: {_downloadService.Status}).";
                CompleteOnce(error);
            }

            await DisposeTerminalResourcesAsync().ConfigureAwait(false);
        }
    }

    private void CompleteOnce(string? error)
    {
        if (Interlocked.CompareExchange(ref _terminalSignaled, 1, 0) != 0)
        {
            return;
        }

        if (error == null)
        {
            Volatile.Write(ref _terminalSucceeded, 1);
        }

        _settingsCancellation.Cancel(false);

        try
        {
            DownloadComplete?.Invoke(this, new() { Error = error });
        }
        catch (Exception ex)
        {
            _logger.Error(
                "A completion subscriber failed for {DownloadSource} ({ExceptionType})",
                _downloadSource,
                ex.GetType().Name);
        }
    }

    private void CancelDownloadSource()
    {
        lock (_downloadCancellationLock)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _downloadCancellation.Cancel(false);
            }
        }
    }

    private async Task DisposeTerminalResourcesAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        _settingsCancellation.Cancel(false);

        try
        {
            if (_settingsTask != null)
            {
                try
                {
                    await _settingsTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        "Unable to finish the settings timer for {DownloadSource} ({ExceptionType})",
                        _downloadSource,
                        ex.GetType().Name);
                }
            }

            _downloadService.DownloadProgressChanged -= OnDownloadProgressChanged;
            _downloadService.DownloadFileCompleted -= OnDownloadFileCompleted;

            try
            {
                await _downloadService.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "Unable to dispose downloader resources for {DownloadSource} ({ExceptionType})",
                    _downloadSource,
                    ex.GetType().Name);
            }

            if (Volatile.Read(ref _terminalSucceeded) == 0)
            {
                await DeleteTemporaryFile().ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_downloadCancellationLock)
            {
                _downloadCancellation.Dispose();
            }

            _settingsCancellation.Dispose();
        }
    }

    private async Task DeleteTemporaryFile()
    {
        var temporaryFilePath = _filePath + _downloadConfiguration.DownloadFileExtension;

        try
        {
            await FileHelper.Delete(temporaryFilePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Unable to remove incomplete download file {FilePath}", temporaryFilePath);
        }
    }

    private async Task StartSettingsTimer()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        try
        {
            while (await timer.WaitForNextTickAsync(_settingsCancellation.Token).ConfigureAwait(false))
            {
                ApplySpeedLimit(
                    _downloadConfiguration,
                    Settings.Get.Downloads.SpeedLimit,
                    TorrentRunner.ActiveDownloadClients.Count);
            }
        }
        catch (OperationCanceledException) when (_settingsCancellation.IsCancellationRequested)
        {
        }
    }
}
