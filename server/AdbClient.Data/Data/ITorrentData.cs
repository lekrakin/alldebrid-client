using AdbClient.Data.Enums;
using AdbClient.Data.Models.Data;

namespace AdbClient.Data.Data;

public interface ITorrentData
{
    Task<IList<Torrent>> Get();
    Task<Torrent?> GetById(Guid torrentId);
    Task<Torrent?> GetByHash(string hash);

    Task<Torrent> Add(string? rdId,
                      string hash,
                      string? fileOrMagnetContents,
                      bool isFile,
                      DownloadClient downloadClient,
                      Torrent torrent);

    Task UpdateRdData(Torrent torrent);
    Task UpdateRdId(Torrent torrent, string rdId);
    Task Update(Torrent torrent);
    Task UpdateCategory(Guid torrentId, string? category);
    Task UpdateComplete(Guid torrentId, string? error, DateTimeOffset? datetime, bool retry);
    Task UpdateFilesSelected(Guid torrentId, DateTimeOffset datetime);
    Task UpdatePriority(Guid torrentId, int? priority);
    Task UpdateRetry(Guid torrentId, DateTimeOffset? dateTime, int retryCount);
    Task UpdateError(Guid torrentId, string error);
    Task FinalizeRetainedDeletion(
        Guid torrentId,
        bool hideFromQbittorrent,
        bool consumeFinishedAction,
        bool markAsDeleted);
    Task Delete(Guid torrentId);
}
