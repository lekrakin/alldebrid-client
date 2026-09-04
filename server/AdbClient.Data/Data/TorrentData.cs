using AdbClient.Data.Enums;
using AdbClient.Data.Models.Data;
using Microsoft.EntityFrameworkCore;

namespace AdbClient.Data.Data;

public class TorrentData(DataContext dataContext) : ITorrentData
{
    private static IList<Torrent>? _torrentCache;

    private static readonly SemaphoreSlim TorrentCacheLock = new(1, 1);

    public async Task<IList<Torrent>> Get()
    {
        await TorrentCacheLock.WaitAsync();

        try
        {
            _torrentCache ??= await dataContext.Torrents
                                                .AsNoTracking()
                                                .Include(m => m.Downloads)
                                                .ToListAsync();

            return [.. _torrentCache.OrderBy(m => m.Priority ?? 9999).ThenBy(m => m.Added)];
        }
        finally
        {
            TorrentCacheLock.Release();
        }
    }

    public async Task<Torrent?> GetById(Guid torrentId)
    {
        var dbTorrent = await dataContext.Torrents
                                          .AsNoTracking()
                                          .Include(m => m.Downloads)
                                          .FirstOrDefaultAsync(m => m.TorrentId == torrentId);

        if (dbTorrent == null) return null;

        foreach (var file in dbTorrent.Downloads)
        {
            file.Torrent = null;
        }

        return dbTorrent;
    }

    public async Task<Torrent?> GetByHash(string hash)
    {
        hash = hash.ToLower();

        var dbTorrent = await dataContext.Torrents
                                          .AsNoTracking()
                                          .Include(m => m.Downloads)
                                          .FirstOrDefaultAsync(m => m.Hash == hash);

        if (dbTorrent == null) return null;

        foreach (var file in dbTorrent.Downloads)
        {
            file.Torrent = null;
        }

        return dbTorrent;
    }

    public async Task<Torrent> Add(string? rdId,
                                   string hash,
                                   string? fileOrMagnetContents,
                                   bool isFile,
                                   DownloadClient downloadClient,
                                   Torrent torrent)
    {
        var settings = SettingData.Get;
        var hasCapturedLocalPath = !string.IsNullOrWhiteSpace(torrent.LocalDownloadPath);
        var localDownloadPath = hasCapturedLocalPath
            ? torrent.LocalDownloadPath
            : settings.Storage.DownloadPath;
        var clientReportedDownloadPath = !string.IsNullOrWhiteSpace(torrent.ClientReportedDownloadPath)
            ? torrent.ClientReportedDownloadPath
            : hasCapturedLocalPath
                ? localDownloadPath
                : string.IsNullOrWhiteSpace(settings.Integrations.ReportedDownloadPath)
                    ? localDownloadPath
                    : settings.Integrations.ReportedDownloadPath;

        var newTorrent = new Torrent
        {
            TorrentId = Guid.NewGuid(),
            Added = DateTimeOffset.UtcNow,
            RdId = rdId,
            Hash = hash.ToLower(),
            Category = torrent.Category,
            LocalDownloadPath = localDownloadPath,
            ClientReportedDownloadPath = clientReportedDownloadPath,
            HostDownloadAction = torrent.HostDownloadAction,
            FinishedActionDelay = torrent.FinishedActionDelay,
            DownloadAction = torrent.DownloadAction,
            FinishedAction = torrent.FinishedAction,
            DownloadMinSize = torrent.DownloadMinSize,
            IncludeRegex = torrent.IncludeRegex,
            ExcludeRegex = torrent.ExcludeRegex,
            DownloadManualFiles = torrent.DownloadManualFiles,
            DownloadClient = downloadClient,
            FileOrMagnet = fileOrMagnetContents,
            IsFile = isFile,
            Priority = torrent.Priority,
            TorrentRetryAttempts = torrent.TorrentRetryAttempts,
            DownloadRetryAttempts = torrent.DownloadRetryAttempts,
            DeleteOnError = torrent.DeleteOnError,
            Lifetime = torrent.Lifetime,
            RdStatus = torrent.RdStatus,
            RdName = torrent.RdName
        };

        await dataContext.Torrents.AddAsync(newTorrent);
        await dataContext.SaveChangesAsync();
        await VoidCache();

        return newTorrent;
    }

    public Task UpdateRdData(Torrent torrent) =>
        Patch(torrent.TorrentId, db =>
        {
            db.RdName = torrent.RdName;
            db.RdSize = torrent.RdSize;
            db.RdHost = torrent.RdHost;
            db.RdSplit = torrent.RdSplit;
            db.RdProgress = torrent.RdProgress;
            db.RdStatus = torrent.RdStatus;
            db.RdStatusRaw = torrent.RdStatusRaw;
            db.RdAdded = torrent.RdAdded;
            db.RdEnded = torrent.RdEnded;
            db.RdSpeed = torrent.RdSpeed;
            db.RdSeeders = torrent.RdSeeders;
            db.RdFiles = torrent.RdFiles;
        });

    public Task UpdateRdId(Torrent torrent, string rdId) =>
        Patch(torrent.TorrentId, db => db.RdId = rdId);

    public Task Update(Torrent torrent) =>
        Patch(torrent.TorrentId, db =>
        {
            db.DownloadClient = torrent.DownloadClient;
            db.HostDownloadAction = torrent.HostDownloadAction;
            db.Category = torrent.Category;
            db.Priority = torrent.Priority;
            db.DownloadRetryAttempts = torrent.DownloadRetryAttempts;
            db.TorrentRetryAttempts = torrent.TorrentRetryAttempts;
            db.DeleteOnError = torrent.DeleteOnError;
            db.Lifetime = torrent.Lifetime;
        });

    public Task UpdateCategory(Guid torrentId, string? category) =>
        Patch(torrentId, db => db.Category = category);

    public async Task UpdateComplete(Guid torrentId, string? error, DateTimeOffset? datetime, bool retry)
    {
        var dbTorrent = await dataContext.Torrents.FirstOrDefaultAsync(m => m.TorrentId == torrentId);
        if (dbTorrent == null) return;

        if (string.IsNullOrWhiteSpace(error))
        {
            var downloads = await dataContext.Downloads.AsNoTracking().Where(m => m.TorrentId == torrentId).ToListAsync();
            var failedCount = downloads.Count(m => !string.IsNullOrWhiteSpace(m.Error));

            if (failedCount > 0)
            {
                error = $"{failedCount}/{downloads.Count} downloads failed with errors";
            }
        }

        if (!string.IsNullOrWhiteSpace(error) && retry && dbTorrent.RetryCount < dbTorrent.TorrentRetryAttempts)
        {
            dbTorrent.RetryCount += 1;
            dbTorrent.Retry = DateTime.UtcNow;
        }

        dbTorrent.Completed = datetime;
        dbTorrent.Error = error;

        await dataContext.SaveChangesAsync();
        await VoidCache();
    }

    public Task UpdateFilesSelected(Guid torrentId, DateTimeOffset datetime) =>
        Patch(torrentId, db => db.FilesSelected = datetime);

    public Task UpdatePriority(Guid torrentId, int? priority) =>
        Patch(torrentId, db => db.Priority = priority);

    public Task UpdateRetry(Guid torrentId, DateTimeOffset? dateTime, int retryCount) =>
        Patch(torrentId, db =>
        {
            db.RetryCount = retryCount;
            db.Retry = dateTime;
        });

    public Task UpdateError(Guid torrentId, string error) =>
        Patch(torrentId, db => db.Error = error);

    public Task FinalizeRetainedDeletion(
        Guid torrentId,
        bool hideFromQbittorrent,
        bool providerDeleted,
        bool markAsDeleted) =>
        Patch(torrentId, db =>
        {
            if (hideFromQbittorrent)
            {
                db.QbittorrentHidden = true;
            }

            if (providerDeleted)
            {
                db.FinishedAction = TorrentFinishedAction.None;
                db.RdId = null;
            }

            if (markAsDeleted)
            {
                db.Completed = DateTimeOffset.UtcNow;
                db.Error = "Torrent deleted";
                db.Retry = null;
            }
        });

    public async Task<Torrent?> ReactivateFromQbittorrent(
        Guid torrentId,
        Torrent requestedDefaults,
        IReadOnlySet<Guid>? downloadsToReset,
        bool providerDeleted)
    {
        var dbTorrent = await dataContext.Torrents
                                         .Include(torrent => torrent.Downloads)
                                         .FirstOrDefaultAsync(torrent => torrent.TorrentId == torrentId);

        if (dbTorrent == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(requestedDefaults.Category) &&
            !string.Equals(dbTorrent.Category, requestedDefaults.Category, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Torrent {dbTorrent.Hash} already exists under a different category.");
        }

        if (!dbTorrent.QbittorrentHidden)
        {
            return dbTorrent;
        }

        Download[] discardedDownloads = [];

        if (downloadsToReset != null)
        {
            var fileOrMagnet = string.IsNullOrWhiteSpace(dbTorrent.FileOrMagnet)
                ? requestedDefaults.FileOrMagnet
                : dbTorrent.FileOrMagnet;

            if ((providerDeleted || dbTorrent.RdId == null) &&
                string.IsNullOrWhiteSpace(fileOrMagnet))
            {
                throw new InvalidOperationException(
                    "The retained torrent cannot be requeued because its original torrent metadata is unavailable.");
            }

            var now = DateTimeOffset.UtcNow;

            if (string.IsNullOrWhiteSpace(dbTorrent.FileOrMagnet))
            {
                dbTorrent.FileOrMagnet = fileOrMagnet;
                dbTorrent.IsFile = requestedDefaults.IsFile;
            }

            dbTorrent.Added = now;
            dbTorrent.Completed = null;
            dbTorrent.Error = null;
            dbTorrent.Retry = null;
            dbTorrent.RetryCount = 0;
            dbTorrent.DownloadClient = requestedDefaults.DownloadClient;
            dbTorrent.HostDownloadAction = requestedDefaults.HostDownloadAction;
            dbTorrent.DownloadAction = requestedDefaults.DownloadAction;
            dbTorrent.FinishedAction = requestedDefaults.FinishedAction;
            dbTorrent.FinishedActionDelay = requestedDefaults.FinishedActionDelay;
            dbTorrent.DownloadMinSize = requestedDefaults.DownloadMinSize;
            dbTorrent.IncludeRegex = requestedDefaults.IncludeRegex;
            dbTorrent.ExcludeRegex = requestedDefaults.ExcludeRegex;
            dbTorrent.DownloadManualFiles = requestedDefaults.DownloadManualFiles;
            dbTorrent.TorrentRetryAttempts = requestedDefaults.TorrentRetryAttempts;
            dbTorrent.DownloadRetryAttempts = requestedDefaults.DownloadRetryAttempts;
            dbTorrent.DeleteOnError = requestedDefaults.DeleteOnError;
            dbTorrent.Lifetime = requestedDefaults.Lifetime;
            dbTorrent.Priority = requestedDefaults.Priority;

            if (providerDeleted)
            {
                dbTorrent.RdId = null;
            }

            if (dbTorrent.RdId == null)
            {
                dbTorrent.FilesSelected = null;
                dbTorrent.RdAdded = null;
                dbTorrent.RdEnded = null;
                dbTorrent.RdProgress = null;
                dbTorrent.RdSpeed = null;
                dbTorrent.RdSeeders = null;
                dbTorrent.RdFiles = null;
                dbTorrent.RdStatus = TorrentStatus.Queued;
                dbTorrent.RdStatusRaw = null;
                discardedDownloads = dbTorrent.Downloads.ToArray();
                dataContext.Downloads.RemoveRange(discardedDownloads);
            }
            else if (requestedDefaults.HostDownloadAction == TorrentHostDownloadAction.DownloadNone)
            {
                discardedDownloads = dbTorrent.Downloads
                                              .Where(download => downloadsToReset.Contains(download.DownloadId))
                                              .ToArray();
                dataContext.Downloads.RemoveRange(discardedDownloads);
            }
            else
            {
                if (dbTorrent.Downloads.Count == 0)
                {
                    dbTorrent.FilesSelected = null;
                }

                foreach (var download in dbTorrent.Downloads.Where(download => downloadsToReset.Contains(download.DownloadId)))
                {
                    download.Added = now;
                    download.DownloadQueued = now;
                    download.DownloadStarted = null;
                    download.DownloadFinished = null;
                    download.UnpackingQueued = null;
                    download.UnpackingStarted = null;
                    download.UnpackingFinished = null;
                    download.Completed = null;
                    download.Error = null;
                    download.RetryCount = 0;
                    download.Link = null;
                    download.RemoteId = null;
                }
            }
        }

        dbTorrent.QbittorrentHidden = false;

        await dataContext.SaveChangesAsync();

        foreach (var download in discardedDownloads)
        {
            dbTorrent.Downloads.Remove(download);
        }

        await VoidCache();

        return dbTorrent;
    }

    public async Task Delete(Guid torrentId)
    {
        var dbTorrent = await dataContext.Torrents
                                         .Include(torrent => torrent.Downloads)
                                         .FirstOrDefaultAsync(torrent => torrent.TorrentId == torrentId);
        if (dbTorrent == null) return;

        dataContext.Downloads.RemoveRange(dbTorrent.Downloads);
        dataContext.Torrents.Remove(dbTorrent);
        await dataContext.SaveChangesAsync();
        await VoidCache();
    }

    public static async Task VoidCache()
    {
        await TorrentCacheLock.WaitAsync();

        try
        {
            _torrentCache = null;
        }
        finally
        {
            TorrentCacheLock.Release();
        }
    }

    private async Task Patch(Guid torrentId, Action<Torrent> mutate)
    {
        var db = await dataContext.Torrents.FirstOrDefaultAsync(m => m.TorrentId == torrentId);
        if (db == null) return;
        mutate(db);
        await dataContext.SaveChangesAsync();
        await VoidCache();
    }
}
