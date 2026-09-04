using AdbClient.Data.Enums;
using AdbClient.Data.Models.Data;
using AdbClient.Service.Helpers;
using AdbClient.Service.Services.Downloaders;

namespace AdbClient.Service.Services;

public class DownloadClient(Download download, Torrent torrent, string destinationPath)
{
    private static long _totalBytesDownloadedThisSession;
    private static readonly Lock TotalBytesDownloadedLock = new();
    private readonly TaskCompletionSource<DownloadCompleteEventArgs> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public IDownloader? Downloader;

    public Data.Enums.DownloadClient Type { get; private set; }

    public bool Finished { get; private set; }

    public string? Error { get; private set; }

    public long Speed { get; private set; }
    public long BytesTotal { get; private set; }
    public long BytesDone { get; private set; }

    private long LastBytesDone { get; set; }

    public async Task<string> Start()
    {
        BytesDone = 0;
        BytesTotal = 0;
        Speed = 0;

        try
        {
            Type = torrent.DownloadClient;

            if (download.Link == null)
            {
                throw new Exception($"Invalid download link");
            }

            var filePath = DownloadHelper.GetDownloadPath(destinationPath, torrent, download);
            var downloadPath = DownloadHelper.GetDownloadPath(torrent, download);

            if (filePath == null || downloadPath == null)
            {
                throw new Exception("Invalid download path");
            }

            await FileHelper.Delete(filePath);

            Downloader = Type switch
            {
                Data.Enums.DownloadClient.Internal => new InternalDownloader(download.Link, filePath),
                _ => throw new Exception($"Unknown download client {Type}")
            };

            Downloader.DownloadComplete += (_, args) =>
            {
                Finished = true;
                Error ??= args.Error;
                _completion.TrySetResult(args);
            };

            Downloader.DownloadProgress += (_, args) =>
            {
                Speed = args.Speed;
                BytesDone = args.BytesDone;
                BytesTotal = args.BytesTotal;

                var bytesAdded = BytesDone - LastBytesDone;

                LastBytesDone = BytesDone;

                AddToTotalBytesDownloadedThisSession(bytesAdded);
            };

            var result = await Downloader.Download();

            return result;
        }
        catch (Exception ex)
        {
            if (Downloader != null)
            {
                await Downloader.Cancel();
            }

            Finished = true;

            throw new Exception($"An unexpected error occurred preparing download {download.Link} for torrent {torrent.RdName}: {ex.Message}");
        }
    }

    public Task<DownloadCompleteEventArgs> WaitForCompletionAsync(CancellationToken cancellationToken = default)
    {
        return _completion.Task.WaitAsync(cancellationToken);
    }

    public async Task Cancel()
    {
        Finished = true;
        Error = null;

        if (Downloader == null)
        {
            return;
        }
        await Downloader.Cancel();
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
}
