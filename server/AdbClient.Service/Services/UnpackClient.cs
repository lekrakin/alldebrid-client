using AdbClient.Data.Models.Data;
using AdbClient.Service.Helpers;
using SharpCompress.Archives;

namespace AdbClient.Service.Services;

public class UnpackClient
{
    private const int LifecycleReady = 0;
    private const int LifecycleRunning = 1;
    private const int LifecycleCancelledBeforeStart = 2;
    private const int LifecycleFinished = 3;

    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly string _destinationPath;
    private readonly Download _download;
    private readonly Func<string, CancellationToken, Task> _unpackOperation;
    private readonly Torrent _torrent;

    private int _cancellationRequested;
    private int _completionSignaled;
    private int _lifecycleState;

    public UnpackClient(Download download, string destinationPath)
        : this(download, destinationPath, null)
    {
    }

    internal UnpackClient(
        Download download,
        string destinationPath,
        Func<string, CancellationToken, Task>? unpackOperation)
    {
        _download = download;
        _destinationPath = destinationPath;
        _torrent = download.Torrent ?? throw new Exception("Torrent is null");
        _unpackOperation = unpackOperation ?? Unpack;
    }

    public bool Finished => Volatile.Read(ref _lifecycleState) == LifecycleFinished;

    public string? Error { get; private set; }

    public int Progress { get; private set; }

    public void Start()
    {
        if (Interlocked.CompareExchange(
                ref _lifecycleState,
                LifecycleRunning,
                LifecycleReady) != LifecycleReady)
        {
            return;
        }

        Progress = 0;

        try
        {
            var filePath = DownloadHelper.GetDownloadPath(_destinationPath, _torrent, _download) ?? throw new Exception("Invalid download path");

            _ = Task.Run(() => RunUnpack(filePath, _cancellationTokenSource.Token));
        }
        catch (Exception ex)
        {
            var downloadSource = Logger.DescribeDownloadSource(_download);
            var safeError = Logger.DescribeDownloadFailure(ex, _download);
            Error = $"An unexpected error occurred preparing {downloadSource} for torrent {_torrent.RdName}: {safeError}";
            Complete();
        }
    }

    public void Cancel()
    {
        if (Interlocked.CompareExchange(ref _cancellationRequested, 1, 0) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref _lifecycleState,
                LifecycleCancelledBeforeStart,
                LifecycleReady) == LifecycleReady)
        {
            Error = "The unpack was cancelled";
            Complete();
            return;
        }

        if (!Finished)
        {
            _cancellationTokenSource.Cancel();
        }
    }

    public Task WaitForCompletionAsync(CancellationToken cancellationToken = default)
    {
        return _completion.Task.WaitAsync(cancellationToken);
    }

    internal void MarkCancellationUnconfirmed()
    {
        Error ??= "The unpack cancellation could not be confirmed.";
    }

    private async Task RunUnpack(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await _unpackOperation(filePath, cancellationToken);
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
            Error ??= "The unpack was cancelled";
        }
        catch (Exception ex)
        {
            var downloadSource = Logger.DescribeDownloadSource(_download);
            var safeError = Logger.DescribeDownloadFailure(ex, _download);
            Error = $"An unexpected error occurred unpacking {downloadSource} for torrent {_torrent.RdName}: {safeError}";
        }
        finally
        {
            Complete();
        }
    }

    private async Task Unpack(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var extractPath = _destinationPath;
        string? extractPathTemp = null;

        var archiveEntries = await GetArchiveFiles(filePath);
        extractPath = ResolveExtractionPath(_destinationPath, _torrent, archiveEntries);

        if (archiveEntries.Any(m => m.Contains(".r00")))
        {
            extractPathTemp = Path.Combine(extractPath, Guid.NewGuid().ToString());

            if (!Directory.Exists(extractPathTemp))
            {
                Directory.CreateDirectory(extractPathTemp);
            }
        }

        if (extractPathTemp != null)
        {
            await Extract(filePath, extractPathTemp, cancellationToken);

            await FileHelper.Delete(filePath);

            var rarFiles = Directory.GetFiles(extractPathTemp, "*.r00", SearchOption.TopDirectoryOnly);

            foreach (var rarFile in rarFiles)
            {
                var mainRarFile = Path.ChangeExtension(rarFile, ".rar");

                if (File.Exists(mainRarFile))
                {
                    await Extract(mainRarFile, extractPath, cancellationToken);
                }

                await FileHelper.DeleteDirectory(extractPathTemp);
            }
        }
        else
        {
            await Extract(filePath, extractPath, cancellationToken);

            await FileHelper.Delete(filePath);
        }
    }

    private static async Task<IList<string>> GetArchiveFiles(string filePath)
    {
        await using Stream stream = File.OpenRead(filePath);

        using var archive = ArchiveFactory.OpenArchive(stream);

        var entries = archive.Entries
                             .Where(entry => !entry.IsDirectory)
                             .Select(m => m.Key!)
                             .ToList();

        return entries;
    }

    private async Task Extract(string filePath, string extractPath, CancellationToken cancellationToken)
    {
        var parts = ArchiveFactory.GetFileParts(filePath);
        var files = parts.Select(part => new FileInfo(part)).ToList();
        using var archive = ArchiveFactory.OpenArchive(files);
        var entries = archive.Entries.ToList();

        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await entries[index].WriteToDirectoryAsync(extractPath, cancellationToken: cancellationToken);
            Progress = (int)Math.Round((index + 1d) / entries.Count * 100);
        }
    }

    internal static string ResolveExtractionPath(
        string destinationPath,
        Torrent torrent,
        IEnumerable<string> archiveEntries)
    {
        var torrentDirectory = DownloadHelper.GetTorrentDirectoryName(torrent);
        var entrySegments = archiveEntries
                           .Where(entry => !string.IsNullOrWhiteSpace(entry))
                           .Select(entry => entry.Split(
                               ['/', '\\'],
                               StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                           .ToList();
        var archiveContainsOnlyTorrentRoot = entrySegments.Count > 0 && entrySegments.All(segments =>
            segments.Length > 1 &&
            string.Equals(
                FileHelper.RemoveInvalidFileNameChars(segments[0]),
                torrentDirectory,
                StringComparison.OrdinalIgnoreCase));
        var normalizedDestination = FileSystemPath.Normalize(destinationPath);
        var extractPath = archiveContainsOnlyTorrentRoot
            ? normalizedDestination
            : FileSystemPath.Normalize(Path.Combine(normalizedDestination, torrentDirectory));

        if (!FileSystemPath.IsSameOrDescendant(extractPath, normalizedDestination))
        {
            throw new InvalidDataException("Archive extraction path is outside the configured download directory.");
        }

        return extractPath;
    }

    private void Complete()
    {
        if (Interlocked.CompareExchange(ref _completionSignaled, 1, 0) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _cancellationRequested) != 0)
        {
            Error ??= "The unpack was cancelled";
        }

        Volatile.Write(ref _lifecycleState, LifecycleFinished);
        _completion.TrySetResult();
    }
}
