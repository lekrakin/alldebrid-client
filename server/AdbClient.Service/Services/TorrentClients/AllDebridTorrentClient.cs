using System.Diagnostics;
using AllDebridNET;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using AdbClient.Data.Enums;
using AdbClient.Data.Models.TorrentClient;
using AdbClient.Service.Helpers;
using AdbClient.Data.Models.Data;
using File = AllDebridNET.File;
using Torrent = AdbClient.Data.Models.Data.Torrent;

namespace AdbClient.Service.Services.TorrentClients;

public interface IAllDebridNetClientFactory
{
    public IAllDebridNETClient GetClient();
}

public class AllDebridNetClientFactory(ILogger<AllDebridNetClientFactory> logger, IHttpClientFactory httpClientFactory) : IAllDebridNetClientFactory
{
    public IAllDebridNETClient GetClient()
    {
        try
        {
            var apiKey = Settings.Get.Provider.ApiKey;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new Exception("All-Debrid API Key not set in the settings");
            }

            var httpClient = httpClientFactory.CreateClient(AdbClient.Service.DiConfig.ProviderHttpClient);
            httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(Settings.Get.Provider.Timeout, 1));

            var allDebridNetClient = new AllDebridNETClient("AllDebridClient", apiKey, httpClient);

            return allDebridNetClient;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, $"The connection to AllDebrid has timed out: {ex.Message}");

            throw;
        }
        catch (TaskCanceledException ex)
        {
            logger.LogError(ex, $"The connection to AllDebrid has timed out: {ex.Message}");

            throw;
        }
    }
}

public class AllDebridTorrentClient(ILogger<AllDebridTorrentClient> logger, IAllDebridNetClientFactory allDebridNetClientFactory, IDownloadableFileFilter fileFilter) : ITorrentClient
{
    private static readonly long SessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static List<TorrentClientTorrent> _cache = [];
    private static long _sessionCounter = 0;

    private static TorrentClientTorrent Map(Magnet torrent)
    {
        var files = GetFiles(torrent.Files);
        return new()
        {
            Id = torrent.Id.ToString(),
            Filename = torrent.Filename ?? "",
            OriginalFilename = torrent.Filename,
            Hash = torrent.Hash ?? "",
            Bytes = torrent.Size ?? 0,
            OriginalBytes = torrent.Size ?? 0,
            Host = null,
            Split = 0,
            Progress = (long)Math.Round((torrent.Downloaded ?? 0) * 100.0 / (torrent.Size ?? 1)),
            Status = torrent.Status,
            StatusCode = torrent.StatusCode ?? 0,
            Added = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(torrent.UploadDate ?? 0),
            Files = files,
            Links = [],
            Ended = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(torrent.CompletionDate ?? 0),
            Speed = torrent.DownloadSpeed
        };
    }

    public async Task<IList<TorrentClientTorrent>> GetTorrents()
    {
        var results = await allDebridNetClientFactory.GetClient().Magnet.StatusLiveAsync(SessionId, _sessionCounter);

        _sessionCounter = results.Counter;

        if (results.Fullsync == true)
        {
            _cache = (results.Magnets ?? []).Select(Map).ToList();
        }
        else
        {
            // Replace the existing items in the cache with the new ones based on the ID
            foreach (var result in results.Magnets ?? [])
            {
                var existing = _cache.FirstOrDefault(m => m.Id == result.Id.ToString());
                if (existing != null)
                {
                    _cache.Remove(existing);
                }
                _cache.Add(Map(result));
            }
        }

        _cache = _cache.Where(m => m.Status != null).ToList();

        return _cache;
    }

    public async Task<TorrentClientUser> GetUser()
    {
        var user = await allDebridNetClientFactory.GetClient().User.GetAsync() ?? throw new Exception("Unable to get user");

        return new()
        {
            Username = user.Username,
            Expiration = user.PremiumUntil > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(user.PremiumUntil) : null
        };
    }

    public async Task<string> AddMagnet(string magnetLink)
    {
        var result = await allDebridNetClientFactory.GetClient().Magnet.UploadMagnetAsync(magnetLink);

        if (result?.Id == null)
        {
            throw new Exception("Unable to add magnet link");
        }

        var resultId = result.Id.ToString() ?? throw new Exception($"Invalid responseID {result.Id}");

        return resultId;
    }

    public async Task<string> AddFile(Byte[] bytes)
    {
        var result = await allDebridNetClientFactory.GetClient().Magnet.UploadFileAsync(bytes);

        if (result?.Id == null)
        {
            throw new Exception("Unable to add torrent file");
        }

        var resultId = result.Id.ToString() ?? throw new Exception($"Invalid responseID {result.Id}");

        return resultId;
    }

    public Task<IList<TorrentClientAvailableFile>> GetAvailableFiles(string hash)
    {
        return Task.FromResult<IList<TorrentClientAvailableFile>>([]);
    }

    /// <inheritdoc />
    public Task<int?> SelectFiles(Torrent torrent)
    {
        return Task.FromResult<int?>(torrent.Files.Count);
    }

    public async Task Delete(string torrentId)
    {
        await allDebridNetClientFactory.GetClient().Magnet.DeleteAsync(torrentId);
    }

    public async Task<string> Unrestrict(string link)
    {
        var result = await allDebridNetClientFactory.GetClient().Links.DownloadLinkAsync(link);

        if (result.Link == null)
        {
            throw new Exception("Invalid result link");
        }

        return result.Link;
    }

    public async Task<Torrent> UpdateData(Torrent torrent, TorrentClientTorrent? torrentClientTorrent)
    {
        try
        {
            if (torrent.RdId == null)
            {
                return torrent;
            }

            torrentClientTorrent ??= await GetInfo(torrent.RdId);

            if (!string.IsNullOrWhiteSpace(torrentClientTorrent.Filename))
            {
                torrent.RdName = torrentClientTorrent.Filename;
            }

            if (torrentClientTorrent.Bytes > 0)
            {
                torrent.RdSize = torrentClientTorrent.Bytes;
            }

            if (torrentClientTorrent.Files != null && torrentClientTorrent.Files.Count != 0)
            {
                torrent.RdFiles = JsonSerializer.Serialize(torrentClientTorrent.Files);
            }

            torrent.ClientKind = Provider.AllDebrid;
            torrent.RdHost = torrentClientTorrent.Host;
            torrent.RdSplit = torrentClientTorrent.Split;
            torrent.RdProgress = torrentClientTorrent.Progress;
            torrent.RdAdded = torrentClientTorrent.Added;
            torrent.RdEnded = torrentClientTorrent.Ended;
            torrent.RdSpeed = torrentClientTorrent.Speed;
            torrent.RdStatusRaw = torrentClientTorrent.Status;

            torrent.RdStatus = torrentClientTorrent.StatusCode switch
            {
                0 => TorrentStatus.Processing, // Processing	In Queue.
                1 => TorrentStatus.Downloading, // Processing	Downloading.
                2 => TorrentStatus.Downloading, // Processing	Compressing / Moving.
                3 => TorrentStatus.Uploading, // Processing	Uploading.
                4 => TorrentStatus.Finished, // Finished	Ready.
                5 => TorrentStatus.Error, // Error	Upload fail.
                6 => TorrentStatus.Error, // Error	Internal error on unpacking.
                7 => TorrentStatus.Error, // Error	Not downloaded in 20 min.
                8 => TorrentStatus.Error, // Error	File too big.
                9 => TorrentStatus.Error, // 	Error	Internal error.
                10 => TorrentStatus.Error, // Error	Download took more than 72h.
                11 => TorrentStatus.Error, // Error	Deleted on the hoster website
                _ => TorrentStatus.Error
            };
        }
        catch (AllDebridException ex)
        {
            if (ex.ErrorCode == "MAGNET_INVALID_ID")
            {
                torrent.RdStatusRaw = "deleted";
            }
            else
            {
                throw;
            }
        }

        return torrent;
    }

    public async Task<IList<DownloadInfo>?> GetDownloadInfos(Torrent torrent)
    {
        if (torrent.RdId == null)
        {
            return null;
        }
        
        Log($"Getting download links", torrent);
        
        var allFiles = await allDebridNetClientFactory.GetClient().Magnet.FilesAsync(long.Parse(torrent.RdId));

        var files = GetFiles(allFiles);
        
        return files.Where(f => fileFilter.IsDownloadable(torrent, f.Path, f.Bytes) && f.DownloadLink != null).Select(f => new DownloadInfo
        {
            FileName = Path.GetFileName(f.Path),
            RestrictedLink = f.DownloadLink!
        }).ToList();
    }

    /// <inheritdoc />
    public Task<string> GetFileName(Download download)
    {
        // FileName is set in GetDownlaadInfos
        Debug.Assert(download.FileName != null);
        
        return Task.FromResult(download.FileName);
    }

    private async Task<TorrentClientTorrent> GetInfo(string torrentId)
    {
        var result = await allDebridNetClientFactory.GetClient().Magnet.StatusAsync(torrentId) ?? throw new Exception($"Unable to find magnet with ID {torrentId}");

        return Map(result);
    }
    
    private static List<TorrentClientFile> GetFiles(List<File>? files, string parentPath = "")
    {
        if (files == null)
        {
            return [];
        }

        return files.SelectMany(file =>
        {
            var currentPath = string.IsNullOrEmpty(parentPath) 
                ? file.FolderOrFileName 
                : Path.Combine(parentPath, file.FolderOrFileName);

            var result = new List<TorrentClientFile>();

            // If it's a file (has size)
            if (file.Size.HasValue)
            {
                result.Add(new()
                {
                    Path = currentPath,
                    Bytes = file.Size.Value,
                    DownloadLink = file.DownloadLink
                });
            }

            // Process sub-nodes if they exist
            if (file.SubNodes != null)
            {
                result.AddRange(GetFiles(file.SubNodes, currentPath));
            }

            return result;
        }).ToList();
    }

    private void Log(string message, Torrent? torrent = null)
    {
        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogDebug(message);
    }

    public static string? GetSymlinkPath(Torrent torrent, Download download)
    {
        var fileName = DownloadHelper.GetFileName(download);

        if (fileName == null || torrent.RdName == null)
        {
            return null;
        }
        
        var directory = DownloadHelper.GetTorrentDirectoryName(torrent);

        var matchingTorrentFiles = torrent.Files.Where(m => m.Path.EndsWith(fileName)).Where(m => !string.IsNullOrWhiteSpace(m.Path)).ToList();

        if (matchingTorrentFiles.Count == 0)
        {
            throw new Exception($"Could not find file {fileName} in {torrent.RdName}");
        }

        // Torrents with a single file in them don't need to have the `RdName` added
        if (torrent.Files.Count == 1)
        {
            return matchingTorrentFiles[0].Path;
        }

        return Path.Combine(directory, matchingTorrentFiles[0].Path);
    }
}
