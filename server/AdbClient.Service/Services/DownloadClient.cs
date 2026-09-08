using AdbClient.Data.Enums;
using AdbClient.Data.Models.Data;
using AdbClient.Service.Helpers;
using AdbClient.Service.Services.Downloaders;

namespace AdbClient.Service.Services;

public class DownloadClient
{
    private const int LifecycleReady = 0;
    private const int LifecycleRunning = 1;
    private const int LifecycleCancelledBeforeStart = 2;
    private const int LifecycleFinished = 3;

    private static long _totalBytesDownloadedThisSession;
    private static readonly Lock TotalBytesDownloadedLock = new();

    private readonly Download _download;
    private readonly Func<string, string, IDownloader> _downloaderFactory;
    private readonly TaskCompletionSource<DownloadCompleteEventArgs> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _destinationPath;
    private readonly Torrent _torrent;

    private int _cancellationRequested;
    private int _completionSignaled;
    private int _downloaderCancellationIssued;
    private int _lifecycleState;
    private IDownloader? _downloader;

    public DownloadClient(Download download, Torrent torrent, string destinationPath)
        : this(download, torrent, destinationPath, (uri, filePath) => new InternalDownloader(uri, filePath))
    {
    }

    internal DownloadClient(
        Download download,
        Torrent torrent,
        string destinationPath,
        Func<string, string, IDownloader> downloaderFactory)
    {
        _download = download;
        _torrent = torrent;
        _destinationPath = destinationPath;
        _downloaderFactory = downloaderFactory;
    }

    public IDownloader? Downloader => Volatile.Read(ref _downloader);

    public Data.Enums.DownloadClient Type { get; private set; }

    public bool Finished => Volatile.Read(ref _lifecycleState) == LifecycleFinished;

    public string? Error { get; private set; }

    public long Speed { get; private set; }
    public long BytesTotal { get; private set; }
    public long BytesDone { get; private set; }

    private long LastBytesDone { get; set; }

    public async Task<string> Start()
    {
        if (Interlocked.CompareExchange(
                ref _lifecycleState,
                LifecycleRunning,
                LifecycleReady) != LifecycleReady)
        {
            throw new InvalidOperationException("The download client cannot be started more than once or after cancellation.");
        }

        BytesDone = 0;
        BytesTotal = 0;
        Speed = 0;

        try
        {
            Type = _torrent.DownloadClient;

            if (_download.Link == null)
            {
                throw new Exception($"Invalid download link");
            }

            ThrowIfCancellationRequested();

            var filePath = DownloadHelper.GetDownloadPath(_destinationPath, _torrent, _download);
            var downloadPath = DownloadHelper.GetDownloadPath(_torrent, _download);

            if (filePath == null || downloadPath == null)
            {
                throw new Exception("Invalid download path");
            }

            await FileHelper.Delete(filePath);
            ThrowIfCancellationRequested();

            var downloader = Type switch
            {
                Data.Enums.DownloadClient.Internal => _downloaderFactory(_download.Link, filePath),
                _ => throw new Exception($"Unknown download client {Type}")
            };
            Volatile.Write(ref _downloader, downloader);

            downloader.DownloadComplete += (_, args) =>
            {
                Complete(args);
            };

            downloader.DownloadProgress += (_, args) =>
            {
                Speed = args.Speed;
                BytesDone = args.BytesDone;
                BytesTotal = args.BytesTotal;

                var bytesAdded = BytesDone - LastBytesDone;

                LastBytesDone = BytesDone;

                AddToTotalBytesDownloadedThisSession(bytesAdded);
            };

            if (Volatile.Read(ref _cancellationRequested) != 0)
            {
                await CancelDownloaderOnce();
                ThrowIfCancellationRequested();
            }

            var result = await downloader.Download();

            return result;
        }
        catch (Exception ex)
        {
            var safeError = Logger.DescribeDownloadFailure(ex, _download);
            Error = safeError;
            var downloadSource = Logger.DescribeDownloadSource(_download);
            Exception? cancellationError = null;

            try
            {
                await CancelDownloaderOnce();
            }
            catch (Exception cancelException)
            {
                cancellationError = new Exception(Logger.DescribeDownloadFailure(cancelException, _download));
            }

            Complete(new() { Error = safeError });

            var preparationError = new Exception(safeError);

            throw new Exception(
                $"An unexpected error occurred preparing {downloadSource} for torrent {_torrent.RdName}: {safeError}",
                cancellationError == null
                    ? preparationError
                    : new AggregateException(preparationError, cancellationError));
        }
    }

    public Task<DownloadCompleteEventArgs> WaitForCompletionAsync(CancellationToken cancellationToken = default)
    {
        return _completion.Task.WaitAsync(cancellationToken);
    }

    internal void MarkCancellationUnconfirmed()
    {
        Error ??= "The download cancellation could not be confirmed.";
    }

    public async Task Cancel()
    {
        Interlocked.Exchange(ref _cancellationRequested, 1);

        if (Interlocked.CompareExchange(
                ref _lifecycleState,
                LifecycleCancelledBeforeStart,
                LifecycleReady) == LifecycleReady)
        {
            Complete(new() { Error = "The download was cancelled" });
            return;
        }

        await CancelDownloaderOnce();
    }

    public async Task Pause()
    {
        if (Downloader == null)
        {
            return;
        }
        await Downloader.Pause();
    }

    public async Task Resume()
    {
        if (Downloader == null)
        {
            return;
        }
        await Downloader.Resume();
    }

    public static long GetTotalBytesDownloadedThisSession()
    {
        lock (TotalBytesDownloadedLock)
        {
            return _totalBytesDownloadedThisSession;
        }
    }

    private static void AddToTotalBytesDownloadedThisSession(long bytes)
    {
        lock (TotalBytesDownloadedLock)
        {
            _totalBytesDownloadedThisSession += bytes;
        }
    }

    private async Task CancelDownloaderOnce()
    {
        var downloader = Downloader;

        if (downloader == null ||
            Interlocked.CompareExchange(ref _downloaderCancellationIssued, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await downloader.Cancel();
            Complete(new() { Error = "The download was cancelled" });
        }
        catch
        {
            Interlocked.Exchange(ref _downloaderCancellationIssued, 0);
            throw;
        }
    }

    private void Complete(DownloadCompleteEventArgs args)
    {
        if (Interlocked.CompareExchange(ref _completionSignaled, 1, 0) != 0)
        {
            return;
        }

        Error ??= args.Error ?? (Volatile.Read(ref _cancellationRequested) != 0
            ? "The download was cancelled"
            : null);
        Volatile.Write(ref _lifecycleState, LifecycleFinished);
        _completion.TrySetResult(args);
    }

    private void ThrowIfCancellationRequested()
    {
        if (Volatile.Read(ref _cancellationRequested) != 0)
        {
            throw new OperationCanceledException("The download was cancelled before it started.");
        }
    }
}
