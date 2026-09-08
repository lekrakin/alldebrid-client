using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdbClient.Data.Data;
using AdbClient.Data.Enums;
using AdbClient.Data.Helpers;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.Internal;
using AdbClient.Data.Models.TorrentClient;
using AdbClient.Service.BackgroundServices;
using AdbClient.Service.Helpers;
using AdbClient.Service.Services.TorrentClients;
using AdbClient.Service.Wrappers;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using AllDebridException = AllDebridNET.AllDebridException;
using Torrent = AdbClient.Data.Models.Data.Torrent;

namespace AdbClient.Service.Services;

public class Torrents(
    ILogger<Torrents> logger,
    ITorrentData torrentData,
    IDownloads downloads,
    IProcessFactory processFactory,
    IFileSystem fileSystem,
    IEnricher enricher,
    AllDebridTorrentClient allDebridTorrentClient,
    TimeSpan? activeClientStopTimeout = null)
{
    private const string MissingProviderTorrentErrorCode = "MAGNET_INVALID_ID";
    private static readonly TimeSpan DefaultActiveClientStopTimeout = TimeSpan.FromSeconds(10);

    private static readonly SemaphoreSlim DestructiveMutationLock = new(1, 1);
    private static readonly SemaphoreSlim ProviderUpdateLock = new(1, 1);
    private static readonly SemaphoreSlim TorrentAddLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    private ITorrentClient TorrentClient => allDebridTorrentClient;
    private readonly TimeSpan _activeClientStopTimeout = ValidateActiveClientStopTimeout(
        activeClientStopTimeout ?? DefaultActiveClientStopTimeout);

    public async Task<IList<Torrent>> Get()
    {
        var torrents = await torrentData.Get();

        foreach (var torrent in torrents)
        {
            foreach (var download in torrent.Downloads)
            {
                if (TorrentRunner.ActiveDownloadClients.TryGetValue(download.DownloadId, out var downloadClient))
                {
                    download.Speed = downloadClient.Speed;
                    download.BytesTotal = downloadClient.BytesTotal;
                    download.BytesDone = downloadClient.BytesDone;
                }

                if (TorrentRunner.ActiveUnpackClients.TryGetValue(download.DownloadId, out var unpackClient))
                {
                    download.BytesTotal = 100;
                    download.BytesDone = unpackClient.Progress;
                }
            }
        }

        return torrents;
    }

    public async Task<Torrent?> GetByHash(string hash)
    {
        var torrent = await torrentData.GetByHash(hash);

        if (torrent != null)
        {
            await UpdateTorrentClientData(torrent);
        }

        return torrent;
    }

    public async Task UpdateCategory(string hash, string? category)
    {
        await TorrentAddLock.WaitAsync();

        try
        {
            var torrent = await torrentData.GetByHash(hash);

            if (torrent == null)
            {
                return;
            }

            Log($"Update category to {category}", torrent);

            await torrentData.UpdateCategory(torrent.TorrentId, category);
        }
        finally
        {
            TorrentAddLock.Release();
        }
    }

    public Task<Torrent> AddMagnetToDebridQueue(string magnetLink, Torrent torrent)
    {
        return AddMagnetToDebridQueue(magnetLink, torrent, false);
    }

    private async Task<Torrent> AddMagnetToDebridQueue(
        string magnetLink,
        Torrent torrent,
        bool destructiveMutationLockHeld)
    {
        ValidateDownloadFilters(torrent);

        MagnetLink magnet;

        try
        {
            magnet = MagnetLink.Parse(magnetLink);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Invalid magnet link.", ex);
        }

        var enriched = await enricher.EnrichMagnetLink(magnetLink);

        try
        {
            magnet = MagnetLink.Parse(enriched);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Tracker enrichment produced an invalid magnet link.", ex);
        }

        TorrentTrackerPolicy.EnsureAllowed(magnet, Settings.Get.Provider.BannedTrackers);

        torrent.RdStatus = TorrentStatus.Queued;
        torrent.RdName = magnet.Name;

        var hash = magnet.InfoHashes.V1OrV2.ToHex();
        var newTorrent = await AddQueued(
            hash,
            enriched,
            false,
            torrent,
            destructiveMutationLockHeld);

        Log($"Adding {hash} (magnet link) to queue", newTorrent);
        await CopyAddedTorrent(magnet.Name!, magnetLink);

        return newTorrent;
    }

    public Task<Torrent> AddFileToDebridQueue(Byte[] bytes, Torrent torrent)
    {
        return AddFileToDebridQueue(bytes, torrent, false);
    }

    private async Task<Torrent> AddFileToDebridQueue(
        Byte[] bytes,
        Torrent torrent,
        bool destructiveMutationLockHeld)
    {
        ValidateDownloadFilters(torrent);

        MonoTorrent.Torrent monoTorrent;

        try
        {
            monoTorrent = await MonoTorrent.Torrent.LoadAsync(bytes);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Invalid torrent file.", ex);
        }

        var enriched = await enricher.EnrichTorrentBytes(bytes);

        if (!enriched.SequenceEqual(bytes))
        {
            try
            {
                monoTorrent = await MonoTorrent.Torrent.LoadAsync(enriched);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Tracker enrichment produced an invalid torrent file.", ex);
            }
        }

        string fileAsBase64;

        if (enriched.SequenceEqual(bytes))
        {
            fileAsBase64 = Convert.ToBase64String(bytes);
            logger.LogDebug($"bytes {bytes}");
        }
        else
        {
            fileAsBase64 = Convert.ToBase64String(enriched);
            logger.LogDebug($"enriched bytes {enriched}");
        }

        TorrentTrackerPolicy.EnsureAllowed(monoTorrent, Settings.Get.Provider.BannedTrackers);

        torrent.RdStatus = TorrentStatus.Queued;
        torrent.RdName = monoTorrent.Name;

        var hash = monoTorrent.InfoHashes.V1OrV2.ToHex();

        var newTorrent = await AddQueued(
            hash,
            fileAsBase64,
            true,
            torrent,
            destructiveMutationLockHeld);

        Log($"Adding {hash} (torrent file) to queue", newTorrent);

        await CopyAddedTorrent(monoTorrent.Name, bytes);

        return newTorrent;
    }

    private static void ValidateDownloadFilters(Torrent torrent)
    {
        ValidateRegex(torrent.IncludeRegex, "include");
        ValidateRegex(torrent.ExcludeRegex, "exclude");
    }

    private static void ValidateRegex(string? pattern, string name)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return;
        }

        try
        {
            _ = BoundedRegex.Create(pattern);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid {name} regular expression.", name, ex);
        }
    }

    private async Task CopyAddedTorrent(string torrentName, Object fileOrMagnet)
    {
        if (!string.IsNullOrWhiteSpace(Settings.Get.Integrations.AddedTorrentCopyPath))
        {
            try
            {
                if (!Directory.Exists(Settings.Get.Integrations.AddedTorrentCopyPath))
                {
                    Directory.CreateDirectory(Settings.Get.Integrations.AddedTorrentCopyPath);
                }

                var copyFileName = Path.Combine(Settings.Get.Integrations.AddedTorrentCopyPath, FileHelper.RemoveInvalidFileNameChars(torrentName));

                copyFileName = fileOrMagnet switch
                {
                    string => $"{copyFileName}.magnet",
                    Byte[] => $"{copyFileName}.torrent",
                    _ => throw new ArgumentException("Unexpected type for fileOrMagnet")
                };

                if (File.Exists(copyFileName))
                {
                    File.Delete(copyFileName);
                }

                switch (fileOrMagnet)
                {
                    case string magnetLink:
                        await File.WriteAllTextAsync(copyFileName, magnetLink);
                        break;
                    case Byte[] torrentFile:
                        await File.WriteAllBytesAsync(copyFileName, torrentFile);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Unable to copy added torrent metadata to {Settings.Get.Integrations.AddedTorrentCopyPath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Adds torrent in database to debrid provider and updates database accordingly.
    /// </summary>
    /// <param name="torrent">The torrent from the database to upload to the debrid provider</param>
    /// <returns>Updated torrent</returns>
    /// <exception cref="Exception">When RdId is not null or FileOrMagnet is null.</exception>
    public async Task DequeueFromDebridQueue(Torrent torrent)
    {
        if (torrent.RdId != null)
        {
            throw new Exception("Torrent already added to debrid provider, cannot dequeue");
        }

        if (torrent.FileOrMagnet == null)
        {
            throw new Exception("Torrent has no torrent file or magnet link");
        }

        logger.LogDebug("Adding {hash} to debrid provider {torrentInfo}", torrent.Hash, torrent.ToLog());

        await ProviderUpdateLock.WaitAsync();

        try
        {
            var id = torrent.IsFile
                ? await TorrentClient.AddFile(Convert.FromBase64String(torrent.FileOrMagnet))
                : await TorrentClient.AddMagnet(torrent.FileOrMagnet);

            await torrentData.UpdateRdId(torrent, id);

            await UpdateTorrentClientData(torrent);
        }
        finally
        {
            ProviderUpdateLock.Release();
        }
    }

    public async Task<IList<TorrentClientAvailableFile>> GetAvailableFiles(string hash)
    {
        var result = await TorrentClient.GetAvailableFiles(hash);

        return result;
    }

    public async Task SelectFiles(Guid torrentId)
    {
        var torrent = await GetById(torrentId);

        if (torrent == null)
        {
            return;
        }

        var selected = await TorrentClient.SelectFiles(torrent);

        if (selected == 0)
        {
            await MarkAllFilesExcluded(torrent);
        }
    }

    public async Task CreateDownloads(Guid torrentId)
    {
        var torrent = await GetById(torrentId);

        if (torrent == null)
        {
            return;
        }

        var downloadInfos = await TorrentClient.GetDownloadInfos(torrent);

        if (downloadInfos == null)
        {
            return;
        }

        if (downloadInfos.Count == 0)
        {
            await MarkAllFilesExcluded(torrent);

            return;
        }

        foreach (var downloadInfo in downloadInfos)
        {
            // Make sure downloads don't get added multiple times
            var downloadExists = await downloads.Get(torrent.TorrentId, downloadInfo.RestrictedLink);

            if (downloadExists == null && !string.IsNullOrWhiteSpace(downloadInfo.RestrictedLink))
            {
                await downloads.Add(torrent.TorrentId, downloadInfo);
            }
        }
    }

    /// <summary>
    /// Logs a message to the console, sets the error on the torrent and ensures it is not retried.
    /// </summary>
    /// <param name="torrent">The torrent to mark as "All files excluded"</param>
    private async Task MarkAllFilesExcluded(Torrent torrent)
    {
        logger.LogInformation("All files excluded by filters (IncludeRegex: {includeRegex}, ExcludeRegex: {excludeRegex}, DownloadMinSize: {downloadMinSize}) {torrentInfo}",
                              torrent.IncludeRegex,
                              torrent.ExcludeRegex,
                              torrent.DownloadMinSize,
                              torrent.ToLog());

        await torrentData.UpdateRetry(torrent.TorrentId, null, torrent.TorrentRetryAttempts);
        await torrentData.UpdateComplete(torrent.TorrentId, "All files excluded", DateTimeOffset.Now, false);
    }

    public async Task Delete(
        Guid torrentId,
        bool deleteData,
        bool deleteRdTorrent,
        bool deleteLocalFiles,
        bool hideFromQbittorrent = false)
    {
        var hasDeletionEffects = deleteData || deleteRdTorrent || deleteLocalFiles;

        if (!hasDeletionEffects && !hideFromQbittorrent)
        {
            return;
        }

        await DestructiveMutationLock.WaitAsync();

        try
        {
            await TorrentAddLock.WaitAsync();

            try
            {
                await DeleteCore(
                    torrentId,
                    deleteData,
                    deleteRdTorrent,
                    deleteLocalFiles,
                    hideFromQbittorrent);
            }
            finally
            {
                TorrentAddLock.Release();
            }
        }
        finally
        {
            DestructiveMutationLock.Release();
        }
    }

    private async Task DeleteCore(
        Guid torrentId,
        bool deleteData,
        bool deleteRdTorrent,
        bool deleteLocalFiles,
        bool hideFromQbittorrent = false)
    {
        var hasDeletionEffects = deleteData || deleteRdTorrent || deleteLocalFiles;

        var torrent = await torrentData.GetById(torrentId);

        if (torrent == null)
        {
            return;
        }

        var localDownloadPath = deleteLocalFiles && !string.IsNullOrWhiteSpace(torrent.RdName)
            ? GetSafeLocalDeletePath(torrent)
            : null;

        Log("Deleting", torrent);

        if (hasDeletionEffects)
        {
            foreach (var download in torrent.Downloads)
            {
                await StopActiveWork(download, torrent);
            }
        }

        if (localDownloadPath != null)
        {
            await DeleteLocalFiles(torrent, localDownloadPath);
        }

        if (deleteRdTorrent && torrent.RdId != null)
        {
            await DeleteProviderTorrent(torrent);
        }

        if (deleteData)
        {
            Log("Deleting AllDebrid Client data", torrent);
            await torrentData.Delete(torrentId);
            return;
        }

        await torrentData.FinalizeRetainedDeletion(
            torrentId,
            hideFromQbittorrent,
            deleteRdTorrent,
            hasDeletionEffects && !torrent.Completed.HasValue);
    }

    private async Task DeleteProviderTorrent(Torrent torrent)
    {
        if (torrent.RdId == null)
        {
            return;
        }

        Log("Deleting torrent from AllDebrid", torrent);

        try
        {
            await TorrentClient.Delete(torrent.RdId);
        }
        catch (AllDebridException ex) when (string.Equals(
                   ex.ErrorCode,
                   MissingProviderTorrentErrorCode,
                   StringComparison.Ordinal))
        {
            logger.LogDebug(
                "AllDebrid torrent {ProviderTorrentId} was already absent while deleting {TorrentHash}",
                torrent.RdId,
                torrent.Hash);
        }
    }

    private async Task StopActiveClient<TClient>(
        ConcurrentDictionary<Guid, TClient> activeClients,
        Download download,
        Torrent torrent,
        string activity,
        Func<TClient, Task> cancel,
        Func<TClient, CancellationToken, Task> waitForCompletion,
        Action<TClient> markCancellationUnconfirmed)
        where TClient : class
    {
        if (!activeClients.TryRemove(download.DownloadId, out var activeClient))
        {
            return;
        }

        var completionConfirmed = false;
        using var timeout = new CancellationTokenSource(_activeClientStopTimeout);

        try
        {
            Log($"Cancelling {activity}", download, torrent);
            await cancel(activeClient).WaitAsync(timeout.Token);
            await waitForCompletion(activeClient, timeout.Token);
            completionConfirmed = true;
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for the active {activity} to stop for download {download.DownloadId}. No files or records were deleted.",
                ex);
        }
        finally
        {
            if (!completionConfirmed)
            {
                markCancellationUnconfirmed(activeClient);
                activeClients.TryAdd(download.DownloadId, activeClient);
            }
        }
    }

    private static TimeSpan ValidateActiveClientStopTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeClientStopTimeout),
                "The active-client stop timeout must be greater than zero.");
        }

        return timeout;
    }

    private async Task StopActiveWork(Download download, Torrent torrent)
    {
        await StopActiveClient(
            TorrentRunner.ActiveDownloadClients,
            download,
            torrent,
            "download",
            client => client.Cancel(),
            (client, cancellationToken) => client.WaitForCompletionAsync(cancellationToken),
            client => client.MarkCancellationUnconfirmed());

        await StopActiveClient(
            TorrentRunner.ActiveUnpackClients,
            download,
            torrent,
            "unpack",
            client =>
            {
                client.Cancel();
                return Task.CompletedTask;
            },
            (client, cancellationToken) => client.WaitForCompletionAsync(cancellationToken),
            client => client.MarkCancellationUnconfirmed());
    }

    internal async Task DeleteLocalFiles(Torrent torrent)
    {
        if (string.IsNullOrWhiteSpace(torrent.RdName))
        {
            return;
        }

        await DeleteLocalFiles(torrent, GetSafeLocalDeletePath(torrent));
    }

    private async Task DeleteLocalFiles(Torrent torrent, string localDownloadPath)
    {
        Log($"Deleting local files in {localDownloadPath}", torrent);

        if (!fileSystem.Directory.Exists(localDownloadPath))
        {
            return;
        }

        var retry = 0;

        while (true)
        {
            try
            {
                fileSystem.Directory.Delete(localDownloadPath, true);
                return;
            }
            catch
            {
                retry++;

                if (retry >= 3)
                {
                    throw;
                }

                await Task.Delay(1000);
            }
        }
    }

    public async Task<string> UnrestrictLink(Guid downloadId)
    {
        var download = await downloads.GetById(downloadId) ?? throw new Exception($"Download with ID {downloadId} not found");

        Log("Unrestricting link", download, download.Torrent);

        var unrestrictedLink = await TorrentClient.Unrestrict(download.Path);

        await downloads.UpdateUnrestrictedLink(downloadId, unrestrictedLink);

        return unrestrictedLink;
    }

    /// <summary>
    /// To be called only when <see cref="Data.Models.Data.Download" />.<see cref="Data.Models.Data.Download.FileName" /> is not set by
    /// <see cref="ITorrentClient.GetDownloadInfos" />
    /// </summary>
    public async Task<string> RetrieveFileName(Guid downloadId)
    {
        var download = await downloads.GetById(downloadId) ?? throw new Exception($"Download with ID {downloadId} not found");

        Log($"Retrieving filename for", download, download.Torrent);

        var fileName = await TorrentClient.GetFileName(download);

        await downloads.UpdateFileName(downloadId, fileName);

        return fileName;
    }

    public async Task<Profile> GetProfile()
    {
        var user = await TorrentClient.GetUser();

        var profile = new Profile
        {
            Provider = "AllDebrid",
            UserName = user.Username,
            Expiration = user.Expiration,
            CurrentVersion = UpdateChecker.CurrentVersion,
            LatestVersion = UpdateChecker.LatestVersion,
            UpdateAvailable = UpdateChecker.UpdateAvailable,
            DisableUpdateNotification = Settings.Get.General.DisableUpdateNotifications
        };

        return profile;
    }

    public async Task UpdateRdData()
    {
        await ProviderUpdateLock.WaitAsync();

        var torrents = await Get();

        try
        {
            var rdTorrents = await TorrentClient.GetTorrents();

            foreach (var rdTorrent in rdTorrents)
            {
                var torrent = torrents.FirstOrDefault(m => m.RdId == rdTorrent.Id);

                // Auto import torrents only torrents that have their files selected
                if (torrent == null && Settings.Get.Provider.AutoImport)
                {
                    var newTorrent = new Torrent
                    {
                        Category = Settings.Get.Downloads.Defaults.Category,
                        DownloadClient = Data.Enums.DownloadClient.Internal,
                        HostDownloadAction = Settings.Get.Downloads.Defaults.HostDownloadAction,
                        FinishedActionDelay = Settings.Get.Downloads.Defaults.FinishedActionDelay,
                        FinishedAction = Settings.Get.Downloads.Defaults.FinishedAction,
                        DownloadMinSize = Settings.Get.Downloads.Defaults.MinFileSize,
                        IncludeRegex = Settings.Get.Downloads.Defaults.IncludeRegex,
                        ExcludeRegex = Settings.Get.Downloads.Defaults.ExcludeRegex,
                        TorrentRetryAttempts = Settings.Get.Downloads.Defaults.TorrentRetryAttempts,
                        DownloadRetryAttempts = Settings.Get.Downloads.Defaults.DownloadRetryAttempts,
                        DeleteOnError = Settings.Get.Downloads.Defaults.DeleteOnError,
                        Lifetime = Settings.Get.Downloads.Defaults.TorrentLifetime,
                        Priority = Settings.Get.Downloads.Defaults.Priority > 0 ? Settings.Get.Downloads.Defaults.Priority : null,
                        RdId = rdTorrent.Id
                    };

                    if (newTorrent.RdStatus == TorrentStatus.WaitingForFileSelection)
                    {
                        continue;
                    }

                    torrent = await torrentData.Add(rdTorrent.Id, rdTorrent.Hash, null, false, Data.Enums.DownloadClient.Internal, newTorrent);

                    await UpdateTorrentClientData(torrent, rdTorrent);
                }
                else if (torrent != null)
                {
                    await UpdateTorrentClientData(torrent, rdTorrent);
                }
            }

            await RemoveMissingProviderRecords(torrents, rdTorrents, Settings.Get.Provider);
        }
        finally
        {
            ProviderUpdateLock.Release();
        }
    }

    internal async Task RemoveMissingProviderRecords(
        IEnumerable<Torrent> torrents,
        IEnumerable<TorrentClientTorrent> providerTorrents,
        DbSettingsProvider settings)
    {
        if (!settings.AutoDelete)
        {
            return;
        }

        var providerIds = providerTorrents
            .Select(providerTorrent => providerTorrent.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var torrent in torrents)
        {
            var missingFromProvider = torrent.RdId == null || !providerIds.Contains(torrent.RdId);

            if (missingFromProvider && torrent.RdStatus != TorrentStatus.Queued)
            {
                await Delete(torrent.TorrentId, true, false, false);
            }
        }
    }

    public async Task RetryTorrent(Guid torrentId, int retryCount)
    {
        await DestructiveMutationLock.WaitAsync();

        try
        {
            var torrent = await torrentData.GetById(torrentId);

            if (torrent?.Retry == null)
            {
                return;
            }

            Log($"Retrying Torrent", torrent);

            foreach (var download in torrent.Downloads)
            {
                await StopActiveWork(download, torrent);
            }

            await UpdateComplete(torrent.TorrentId, "Retrying Torrent", DateTimeOffset.UtcNow, false);
            await UpdateRetry(torrent.TorrentId, null, 0);

            foreach (var download in torrent.Downloads)
            {
                await downloads.UpdateError(download.DownloadId, null);
                await downloads.UpdateCompleted(download.DownloadId, DateTimeOffset.UtcNow);
            }

            await DeleteCore(torrentId, true, true, true);

            if (string.IsNullOrWhiteSpace(torrent.FileOrMagnet))
            {
                throw new Exception($"Cannot re-add this torrent, original magnet or file not found");
            }

            Torrent newTorrent;

            if (torrent.IsFile)
            {
                var bytes = Convert.FromBase64String(torrent.FileOrMagnet);

                newTorrent = await AddFileToDebridQueue(bytes, torrent, true);
            }
            else
            {
                newTorrent = await AddMagnetToDebridQueue(torrent.FileOrMagnet, torrent, true);
            }

            await torrentData.UpdateRetry(newTorrent.TorrentId, null, retryCount);
        }
        finally
        {
            DestructiveMutationLock.Release();
        }
    }

    public async Task RetryDownload(Guid downloadId)
    {
        await DestructiveMutationLock.WaitAsync();

        try
        {
            var download = await downloads.GetById(downloadId);

            if (download == null)
            {
                return;
            }

            Log($"Retrying Download", download, download.Torrent);

            await StopActiveWork(download, download.Torrent!);

            var downloadPath = DownloadPath(download.Torrent!);

            var filePath = DownloadHelper.GetDownloadPath(downloadPath, download.Torrent!, download);

            if (filePath != null)
            {
                Log($"Deleting {filePath}", download, download.Torrent);

                await FileHelper.Delete(filePath);
            }

            Log($"Resetting", download, download.Torrent);

            await downloads.Reset(downloadId);

            await torrentData.UpdateComplete(download.TorrentId, null, null, false);
        }
        finally
        {
            DestructiveMutationLock.Release();
        }
    }

    public async Task UpdateComplete(Guid torrentId, string? error, DateTimeOffset datetime, bool retry)
    {
        await torrentData.UpdateComplete(torrentId, error, datetime, retry);
    }

    public async Task UpdateFilesSelected(Guid torrentId, DateTimeOffset datetime)
    {
        await torrentData.UpdateFilesSelected(torrentId, datetime);
    }

    public async Task UpdatePriority(string hash, int priority)
    {
        var torrent = await torrentData.GetByHash(hash);

        if (torrent == null)
        {
            return;
        }

        await torrentData.UpdatePriority(torrent.TorrentId, priority);
    }

    public async Task UpdateRetry(Guid torrentId, DateTimeOffset? datetime, int retry)
    {
        await torrentData.UpdateRetry(torrentId, datetime, retry);
    }

    public async Task UpdateError(Guid torrentId, string error)
    {
        await torrentData.UpdateError(torrentId, error);
    }

    public async Task<Torrent?> GetById(Guid torrentId)
    {
        var torrent = await torrentData.GetById(torrentId);

        if (torrent == null)
        {
            return null;
        }

        await UpdateTorrentClientData(torrent);

        foreach (var download in torrent.Downloads)
        {
            if (TorrentRunner.ActiveDownloadClients.TryGetValue(download.DownloadId, out var downloadClient))
            {
                download.Speed = downloadClient.Speed;
                download.BytesTotal = downloadClient.BytesTotal;
                download.BytesDone = downloadClient.BytesDone;
            }

            if (TorrentRunner.ActiveUnpackClients.TryGetValue(download.DownloadId, out var unpackClient))
            {
                download.BytesTotal = 100;
                download.BytesDone = unpackClient.Progress;
            }
        }

        return torrent;
    }

    internal string DownloadPath(Torrent torrent, DbSettings? settings = null)
    {
        var downloadRoot = string.IsNullOrWhiteSpace(torrent.LocalDownloadPath)
            ? (settings ?? Settings.Get).Storage.DownloadPath
            : torrent.LocalDownloadPath;

        return DownloadHelper.GetCategoryPath(
            downloadRoot,
            torrent.Category,
            fileSystem);
    }

    private string GetSafeLocalDeletePath(Torrent torrent)
    {
        var downloadRoot = FileSystemPath.Normalize(
            string.IsNullOrWhiteSpace(torrent.LocalDownloadPath)
                ? Settings.Get.Storage.DownloadPath
                : torrent.LocalDownloadPath);
        var categoryPath = DownloadPath(torrent);
        var torrentPath = FileSystemPath.Normalize(Path.Combine(
            categoryPath,
            DownloadHelper.GetTorrentDirectoryName(torrent)));

        if (!FileSystemPath.IsStrictDescendant(torrentPath, categoryPath) ||
            !FileSystemPath.IsSameOrDescendant(categoryPath, downloadRoot) ||
            FileSystemPath.ContainsReparsePoint(fileSystem, torrentPath, downloadRoot))
        {
            throw new InvalidDataException("Refusing to delete an unsafe torrent download path.");
        }

        return torrentPath;
    }

    private HashSet<Guid> GetDownloadsToReset(Torrent torrent)
    {
        try
        {
            var jobRoot = GetSafeCapturedJobPath(torrent);
            EnsureDirectoryWhenPresent(jobRoot);
            var categoryRoot = fileSystem.Path.GetDirectoryName(jobRoot)!;
            var downloadsToReset = new HashSet<Guid>();

            foreach (var download in torrent.Downloads)
            {
                var relativePath = DownloadHelper.GetDownloadPath(torrent, download);

                if (relativePath == null)
                {
                    downloadsToReset.Add(download.DownloadId);
                    continue;
                }

                var filePath = FileSystemPath.Normalize(fileSystem.Path.Combine(categoryRoot, relativePath));

                if (!FileSystemPath.IsStrictDescendant(filePath, jobRoot) ||
                    FileSystemPath.ContainsReparsePoint(fileSystem, filePath, jobRoot))
                {
                    throw new InvalidDataException("A retained download path is outside its job boundary or contains a reparse point.");
                }

                var attributes = GetAttributesIfPresent(filePath);

                if (attributes?.HasFlag(FileAttributes.ReparsePoint) == true ||
                    attributes?.HasFlag(FileAttributes.Directory) == true)
                {
                    throw new InvalidDataException("A retained download path is not a regular file.");
                }

                if (attributes == null || !download.Completed.HasValue || !string.IsNullOrWhiteSpace(download.Error))
                {
                    downloadsToReset.Add(download.DownloadId);
                }
            }

            return downloadsToReset;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidDataException(
                "The captured torrent job path could not be inspected safely.",
                ex);
        }
    }

    private string GetSafeCapturedJobPath(Torrent torrent)
    {
        if (string.IsNullOrWhiteSpace(torrent.LocalDownloadPath))
        {
            throw new InvalidDataException(
                "The retained torrent has no captured local download path and cannot be reactivated safely.");
        }

        var downloadRoot = FileSystemPath.Normalize(torrent.LocalDownloadPath);
        var categoryRoot = DownloadHelper.GetCategoryPath(downloadRoot, torrent.Category, fileSystem);
        var jobRoot = FileSystemPath.Normalize(fileSystem.Path.Combine(
            categoryRoot,
            DownloadHelper.GetTorrentDirectoryName(torrent)));

        if (!FileSystemPath.IsStrictDescendant(jobRoot, categoryRoot) ||
            !FileSystemPath.IsSameOrDescendant(categoryRoot, downloadRoot) ||
            FileSystemPath.ContainsReparsePoint(fileSystem, jobRoot, downloadRoot))
        {
            throw new InvalidDataException(
                "The captured torrent job path is outside its download boundary or contains a reparse point.");
        }

        EnsureDirectoryWhenPresent(downloadRoot);
        EnsureDirectoryWhenPresent(categoryRoot);

        return jobRoot;
    }

    private void EnsureDirectoryWhenPresent(string path)
    {
        var attributes = GetAttributesIfPresent(path);

        if (attributes != null &&
            (!attributes.Value.HasFlag(FileAttributes.Directory) ||
             attributes.Value.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new InvalidDataException("A captured torrent path boundary is not a regular directory.");
        }
    }

    private FileAttributes? GetAttributesIfPresent(string path)
    {
        try
        {
            return fileSystem.File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool HasActiveWork(Torrent torrent)
    {
        return torrent.Downloads.Any(download =>
            TorrentRunner.ActiveDownloadClients.ContainsKey(download.DownloadId) ||
            TorrentRunner.ActiveUnpackClients.ContainsKey(download.DownloadId));
    }

    private static bool HasTerminalError(Torrent torrent)
    {
        return !string.IsNullOrWhiteSpace(torrent.Error) ||
               torrent.RdStatus == TorrentStatus.Error;
    }

    private async Task<Torrent> AddQueued(string infoHash,
                                          string fileOrMagnetContents,
                                          bool isFile,
                                          Torrent torrent,
                                          bool destructiveMutationLockHeld)
    {
        if (!destructiveMutationLockHeld)
        {
            await DestructiveMutationLock.WaitAsync();
        }

        try
        {
            await TorrentAddLock.WaitAsync();

            try
            {
                var existingTorrent = await torrentData.GetByHash(infoHash);

                if (existingTorrent != null)
                {
                    if (!string.IsNullOrWhiteSpace(torrent.Category) &&
                        !string.Equals(existingTorrent.Category, torrent.Category, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Torrent {existingTorrent.Hash} already exists under a different category.");
                    }

                    if (!existingTorrent.QbittorrentHidden)
                    {
                        return existingTorrent;
                    }

                    IReadOnlySet<Guid>? downloadsToReset = null;

                    if (!HasActiveWork(existingTorrent) &&
                        (existingTorrent.Completed.HasValue || HasTerminalError(existingTorrent)))
                    {
                        var missingDownloads = GetDownloadsToReset(existingTorrent);

                        if (missingDownloads.Count > 0 || existingTorrent.Downloads.Count == 0 || HasTerminalError(existingTorrent))
                        {
                            downloadsToReset = missingDownloads;
                        }
                    }

                    var providerDeleted = false;

                    if (downloadsToReset != null &&
                        existingTorrent.RdStatus == TorrentStatus.Error &&
                        existingTorrent.RdId != null)
                    {
                        await DeleteProviderTorrent(existingTorrent);
                        providerDeleted = true;
                    }

                    torrent.FileOrMagnet = fileOrMagnetContents;
                    torrent.IsFile = isFile;

                    var reactivated = await torrentData.ReactivateFromQbittorrent(
                        existingTorrent.TorrentId,
                        torrent,
                        downloadsToReset,
                        providerDeleted);

                    if (reactivated == null)
                    {
                        throw new InvalidOperationException(
                            $"Torrent {existingTorrent.Hash} disappeared while it was being reactivated.");
                    }

                    return reactivated;
                }

                return await torrentData.Add(null,
                                             infoHash,
                                             fileOrMagnetContents,
                                             isFile,
                                             torrent.DownloadClient,
                                             torrent);
            }
            finally
            {
                TorrentAddLock.Release();
            }
        }
        finally
        {
            if (!destructiveMutationLockHeld)
            {
                DestructiveMutationLock.Release();
            }
        }
    }

    public async Task Update(Torrent torrent)
    {
        await TorrentAddLock.WaitAsync();

        try
        {
            await torrentData.Update(torrent);
        }
        finally
        {
            TorrentAddLock.Release();
        }
    }

    public async Task RunTorrentComplete(Guid torrentId, DbSettings? settings = null)
    {
        settings ??= Settings.Get;

        if (string.IsNullOrWhiteSpace(settings.Integrations.CompletionCommand.ExecutablePath))
        {
            return;
        }

        var downloadsForTorrent = await downloads.GetForTorrent(torrentId);

        if (downloadsForTorrent.Count == 0)
        {
            logger.LogDebug(
                "Skipping completion command for torrent {TorrentId} because no files were downloaded",
                torrentId);
            return;
        }

        var torrent = await torrentData.GetById(torrentId) ?? throw new Exception($"Cannot find Torrent with ID {torrentId}");

        var fileName = settings.Integrations.CompletionCommand.ExecutablePath;
        var arguments = settings.Integrations.CompletionCommand.Arguments ?? "";

        Log($"Parsing external program {fileName} with arguments {arguments}", torrent);

        var downloadPath = DownloadPath(torrent, settings);
        var torrentPath = Path.Combine(downloadPath, DownloadHelper.GetTorrentDirectoryName(torrent));

        var filePath = torrentPath;

        var files = fileSystem.Directory.GetFiles(filePath);

        if (files.Length == 1)
        {
            filePath = Path.Combine(torrentPath, files[0]);
        }

        arguments = arguments.Replace("%N", $"\"{torrent.RdName}\"");
        arguments = arguments.Replace("%L", $"\"{torrent.Category}\"");
        arguments = arguments.Replace("%F", $"\"{filePath}\"");
        arguments = arguments.Replace("%R", $"\"{downloadPath}\"");
        arguments = arguments.Replace("%D", $"\"{torrentPath}\"");
        arguments = arguments.Replace("%C", downloadsForTorrent.Count.ToString(CultureInfo.InvariantCulture).Replace(",", "").Replace(".", ""));
        arguments = arguments.Replace("%Z", torrent.RdSize?.ToString(CultureInfo.InvariantCulture).Replace(",", "").Replace(".", ""));
        arguments = arguments.Replace("%I", torrent.Hash);

        Log($"Executing external program {fileName} with arguments {arguments}", torrent);

        var errorSb = new StringBuilder();
        var outputSb = new StringBuilder();

        using var process = processFactory.NewProcess();

        process.StartInfo.FileName = fileName;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        process.OutputDataReceived += (_, data) =>
        {
            if (data == null)
            {
                return;
            }

            outputSb.AppendLine(data.Trim());
        };
        process.ErrorDataReceived += (_, data) =>
        {
            if (data == null)
            {
                return;
            }

            errorSb.AppendLine(data.Trim());
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeoutSeconds = settings.Integrations.CompletionCommand.TimeoutSeconds;
        var exited = process.WaitForExit(timeoutSeconds * 1000);

        if (!exited)
        {
            logger.LogWarning(
                "Completion command did not exit within {TimeoutSeconds} seconds; terminating its process tree. {TorrentInfo}",
                timeoutSeconds,
                torrent.ToLog());

            try
            {
                process.Kill(entireProcessTree: true);

                if (!process.WaitForExit(5000))
                {
                    logger.LogWarning(
                        "Completion command termination could not be confirmed within 5 seconds. {TorrentInfo}",
                        torrent.ToLog());
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to terminate timed-out completion command. {TorrentInfo}", torrent.ToLog());
            }
        }

        var errors = errorSb.ToString();
        var output = outputSb.ToString();

        if (errors.Length > 0)
        {
            Log($"External application exited with errors: {errors}", torrent);
        }

        if (output.Length > 0)
        {
            Log($"External application exited with output: {output}", torrent);
        }
    }

    private async Task UpdateTorrentClientData(Torrent torrent, TorrentClientTorrent? torrentClientTorrent = null)
    {
        try
        {
            var originalTorrent = JsonSerializer.Serialize(torrent, JsonSerializerOptions);

            await TorrentClient.UpdateData(torrent, torrentClientTorrent);

            var newTorrent = JsonSerializer.Serialize(torrent, JsonSerializerOptions);

            if (originalTorrent != newTorrent)
            {
                await torrentData.UpdateRdData(torrent);
            }
        }
        catch
        {
            // ignored
        }
    }

    private void Log(string message, Download? download, Torrent? torrent)
    {
        if (download != null)
        {
            message = $"{message} {download.ToLog()}";
        }

        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogDebug(message);
    }

    private void Log(string message, Torrent? torrent = null)
    {
        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogDebug(message);
    }
}
