using System.IO.Abstractions;
using AdbClient.Data.Enums;
using AdbClient.Data.Helpers;
using AdbClient.Data.Models.Data;
using AdbClient.Service.Helpers;
using AdbClient.Service.Models.QBittorrent;
using Microsoft.Extensions.Logging;

namespace AdbClient.Service.Services;

public sealed class QBittorrentCompatibility(
    ILogger<QBittorrentCompatibility> logger,
    Authentication authentication,
    Settings settings,
    Torrents torrents,
    IHttpClientFactory httpClientFactory,
    IFileSystem fileSystem) : IQBittorrentCompatibility
{
    public const int MaxTorrentFileSizeBytes = 32 * 1024 * 1024;

    private const long UnknownEta = 8_640_000;
    public async Task<bool> Login(string userName, string password)
    {
        var result = await authentication.Login(userName, password);
        return result.Succeeded;
    }

    public QBittorrentPreferences GetPreferences()
    {
        return new()
        {
            SavePath = GetSavePath(null)
        };
    }

    public async Task<IReadOnlyDictionary<string, QBittorrentCategory>> GetCategories()
    {
        var configuredCategories = (Settings.Get.Integrations.Categories ?? string.Empty)
                                   .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var assignedCategories = (await torrents.Get())
                                .Where(torrent => !torrent.QbittorrentHidden)
                                .Select(torrent => torrent.Category)
                                .Where(category => !string.IsNullOrWhiteSpace(category))
                                .Select(category => category!);

        return configuredCategories.Concat(assignedCategories)
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .ToDictionary(
                                       category => category,
                                       category => new QBittorrentCategory
                                       {
                                           Name = category,
                                           SavePath = GetSavePath(category)
                                       },
                                       StringComparer.OrdinalIgnoreCase);
    }

    public async Task CreateCategory(string category)
    {
        category = NormalizeCategory(category)
                   ?? throw new ArgumentException("Category cannot be empty.", nameof(category));

        var categories = (Settings.Get.Integrations.Categories ?? string.Empty)
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();

        var existingIndex = categories.FindIndex(value =>
            value.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (existingIndex < 0)
        {
            categories.Add(category);
            await settings.Update("General:Categories", string.Join(',', categories));
        }
        else if (!categories[existingIndex].Equals(category, StringComparison.Ordinal))
        {
            categories[existingIndex] = category;
            await settings.Update("General:Categories", string.Join(',', categories));
        }
    }

    public async Task Add(string urls, string? category, CancellationToken cancellationToken = default)
    {
        var torrentUrls = urls.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (torrentUrls.Length == 0)
        {
            throw new ArgumentException("At least one torrent URL is required.", nameof(urls));
        }

        foreach (var torrentUrl in torrentUrls)
        {
            var normalizedTorrentUrl = NormalizeTorrentUrl(torrentUrl);
            var torrent = CreateTorrent(category);

            if (normalizedTorrentUrl.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            {
                await torrents.AddMagnetToDebridQueue(normalizedTorrentUrl, torrent);
                continue;
            }

            if (!Uri.TryCreate(normalizedTorrentUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("Unsupported torrent URL.", nameof(urls));
            }

            logger.LogDebug(
                "Downloading torrent metadata from {TorrentScheme} origin {TorrentHost} on port {TorrentPort}",
                uri.Scheme,
                uri.IdnHost,
                uri.Port);

            var client = httpClientFactory.CreateClient();
            byte[] fileBytes;

            try
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength > MaxTorrentFileSizeBytes)
                {
                    throw new ArgumentException("Torrent file exceeds the 32 MB limit.", nameof(urls));
                }

                await response.Content.LoadIntoBufferAsync(MaxTorrentFileSizeBytes, cancellationToken);
                fileBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug(
                    "Torrent metadata request failed with {ExceptionType}",
                    ex.GetType().Name);

                // Do not retain the original exception: HTTP exception messages can contain
                // the complete request URI, which may include credentials or passkeys.
                throw new HttpRequestException("Unable to download torrent metadata.", null, ex.StatusCode);
            }

            await torrents.AddFileToDebridQueue(fileBytes, torrent);
        }
    }

    public async Task Add(byte[] torrentBytes, string? category)
    {
        if (torrentBytes.Length == 0)
        {
            throw new ArgumentException("Torrent file cannot be empty.", nameof(torrentBytes));
        }

        if (torrentBytes.Length > MaxTorrentFileSizeBytes)
        {
            throw new ArgumentException("Torrent file exceeds the 32 MB limit.", nameof(torrentBytes));
        }

        var torrent = CreateTorrent(category);
        await torrents.AddFileToDebridQueue(torrentBytes, torrent);
    }

    private static string NormalizeTorrentUrl(string torrentUrl)
    {
        if (!Uri.TryCreate(torrentUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.IdnHost, "nyaa.si", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(uri.Query))
        {
            return torrentUrl;
        }

        const string queryPrefix = "?q=";
        var isValidInfoHashSearch = uri.Scheme == Uri.UriSchemeHttps &&
                                    uri.IsDefaultPort &&
                                    string.IsNullOrEmpty(uri.UserInfo) &&
                                    string.IsNullOrEmpty(uri.Fragment) &&
                                    uri.Query.StartsWith(queryPrefix, StringComparison.Ordinal) &&
                                    uri.Query.Length == queryPrefix.Length + 40;

        if (!isValidInfoHashSearch)
        {
            throw new ArgumentException("Unsupported Nyaa search URL.", nameof(torrentUrl));
        }

        var infoHash = uri.Query[queryPrefix.Length..];

        if (infoHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Unsupported Nyaa search URL.", nameof(torrentUrl));
        }

        return $"magnet:?xt=urn:btih:{infoHash.ToLowerInvariant()}";
    }

    public async Task<IReadOnlyList<QBittorrentTorrentInfo>> GetTorrents(string? category)
    {
        var allTorrents = await torrents.Get();
        var filteredTorrents = allTorrents.Where(torrent => !torrent.QbittorrentHidden);

        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            filteredTorrents = filteredTorrents.Where(torrent =>
                string.Equals(torrent.Category, category, StringComparison.OrdinalIgnoreCase));
        }

        return filteredTorrents.Select(MapTorrent).ToList();
    }

    public async Task<QBittorrentTorrentProperties?> GetProperties(string hash)
    {
        var torrent = await torrents.GetByHash(hash);

        if (torrent == null || torrent.QbittorrentHidden)
        {
            return null;
        }

        return new()
        {
            Hash = torrent.Hash,
            SavePath = GetTorrentSavePath(torrent),
            SeedingTime = 0
        };
    }

    public async Task<IReadOnlyList<QBittorrentTorrentFile>?> GetFiles(string hash)
    {
        var torrent = await torrents.GetByHash(hash);

        if (torrent == null || torrent.QbittorrentHidden)
        {
            return null;
        }

        var filePaths = torrent.Downloads
                               .Select(download => DownloadHelper.GetDownloadPath(torrent, download))
                               .Where(path => !string.IsNullOrWhiteSpace(path))
                               .Select(path => NormalizeTorrentPath(path!))
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();

        if (filePaths.Count == 0)
        {
            filePaths = torrent.Files
                               .Where(file => file.Selected && !string.IsNullOrWhiteSpace(file.Path))
                               .Select(file => NormalizeTorrentPath(file.Path))
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();
        }

        return filePaths.Select(path => new QBittorrentTorrentFile { Name = path }).ToList();
    }

    public async Task SetCategory(string hashes, string? category)
    {
        var normalizedCategory = NormalizeCategory(category);

        foreach (var hash in SplitHashes(hashes))
        {
            var torrent = await torrents.GetByHash(hash);

            if (torrent == null || torrent.QbittorrentHidden)
            {
                continue;
            }

            if (torrent.Downloads.Count > 0 || torrent.Completed.HasValue)
            {
                throw new InvalidOperationException("A torrent category cannot change after its local download has started.");
            }

            await torrents.UpdateCategory(hash, normalizedCategory);
        }
    }

    public async Task SetTopPriority(string hashes)
    {
        foreach (var hash in SplitHashes(hashes))
        {
            var torrent = await torrents.GetByHash(hash);

            if (torrent == null || torrent.QbittorrentHidden)
            {
                continue;
            }

            await torrents.UpdatePriority(hash, 1);
        }
    }

    public async Task Delete(string hashes, bool deleteFiles)
    {
        foreach (var hash in SplitHashes(hashes))
        {
            var torrent = await torrents.GetByHash(hash);

            if (torrent == null || torrent.QbittorrentHidden)
            {
                continue;
            }

            var cleanupPlan = deleteFiles
                ? null
                : CreateEmptyDirectoryCleanupPlan(torrent);

            switch (torrent.FinishedAction)
            {
                case TorrentFinishedAction.RemoveAllTorrents:
                    await torrents.Delete(torrent.TorrentId, true, true, deleteFiles);
                    break;
                case TorrentFinishedAction.RemoveProvider:
                    await torrents.Delete(torrent.TorrentId, false, true, deleteFiles, true);
                    break;
                case TorrentFinishedAction.RemoveClient:
                    await torrents.Delete(torrent.TorrentId, true, false, deleteFiles);
                    break;
                case TorrentFinishedAction.None:
                    await torrents.Delete(torrent.TorrentId, false, false, deleteFiles, true);

                    logger.LogDebug(
                        "Retaining qBittorrent record {TorrentHash} under its configured finished action",
                        torrent.Hash);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(torrent.FinishedAction),
                        torrent.FinishedAction,
                        "Unsupported torrent finished action.");
            }

            if (cleanupPlan != null)
            {
                CleanupEmptyJobDirectories(cleanupPlan);
            }
        }
    }

    private static IEnumerable<string> SplitHashes(string hashes)
    {
        var values = hashes.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (values.Any(hash => hash.Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Bulk selection of every torrent is not supported.", nameof(hashes));
        }

        return values;
    }

    private static string NormalizeTorrentPath(string path)
    {
        return path.TrimStart('/', '\\').Replace('\\', '/');
    }

    private static string? NormalizeCategory(string? category)
    {
        var normalized = TorrentCategory.Normalize(category);

        if (normalized == null)
        {
            return null;
        }

        var downloadRoot = FileSystemPath.Normalize(Settings.Get.Storage.DownloadPath);
        var categoryPath = FileSystemPath.Normalize(Path.Combine(
            downloadRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));

        if (!FileSystemPath.IsStrictDescendant(categoryPath, downloadRoot))
        {
            throw new ArgumentException($"Invalid torrent category: {category}", nameof(category));
        }

        return normalized;
    }

    private EmptyDirectoryCleanupPlan? CreateEmptyDirectoryCleanupPlan(Torrent torrent)
    {
        if ((string.IsNullOrWhiteSpace(torrent.LocalDownloadPath) &&
             string.IsNullOrWhiteSpace(Settings.Get.Storage.DownloadPath)) ||
            string.IsNullOrWhiteSpace(torrent.RdName))
        {
            return null;
        }

        try
        {
            var downloadRoot = FileSystemPath.Normalize(
                string.IsNullOrWhiteSpace(torrent.LocalDownloadPath)
                    ? Settings.Get.Storage.DownloadPath
                    : torrent.LocalDownloadPath);
            var categoryRoot = string.IsNullOrWhiteSpace(torrent.Category)
                ? downloadRoot
                : FileSystemPath.Normalize(fileSystem.Path.Combine(downloadRoot, torrent.Category));

            if (!FileSystemPath.IsSameOrDescendant(categoryRoot, downloadRoot))
            {
                logger.LogWarning(
                    "Skipping qBittorrent directory cleanup because category path {CategoryRoot} is outside download root {DownloadRoot}",
                    categoryRoot,
                    downloadRoot);
                return null;
            }

            var jobRoot = FileSystemPath.Normalize(fileSystem.Path.Combine(
                categoryRoot,
                DownloadHelper.GetTorrentDirectoryName(torrent)));

            if (!FileSystemPath.IsStrictDescendant(jobRoot, categoryRoot))
            {
                logger.LogWarning(
                    "Skipping qBittorrent directory cleanup because job path {JobRoot} is outside category root {CategoryRoot}",
                    jobRoot,
                    categoryRoot);
                return null;
            }

            var candidates = new HashSet<string>(FileSystemPath.Comparer)
            {
                jobRoot
            };

            foreach (var download in torrent.Downloads)
            {
                var relativePath = DownloadHelper.GetDownloadPath(torrent, download);

                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                var filePath = FileSystemPath.Normalize(fileSystem.Path.Combine(categoryRoot, relativePath));
                var directory = fileSystem.Path.GetDirectoryName(filePath);

                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                directory = FileSystemPath.Normalize(directory);

                if (!FileSystemPath.IsSameOrDescendant(directory, jobRoot))
                {
                    logger.LogWarning(
                        "Skipping qBittorrent directory cleanup because download path {DownloadPath} is outside job root {JobRoot}",
                        directory,
                        jobRoot);
                    return null;
                }

                candidates.Add(directory);
            }

            return new(
                downloadRoot,
                categoryRoot,
                candidates.OrderByDescending(path => path.Length).ToList());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to safely resolve qBittorrent cleanup paths for {TorrentHash}", torrent.Hash);
            return null;
        }
    }

    private void CleanupEmptyJobDirectories(EmptyDirectoryCleanupPlan plan)
    {
        foreach (var candidate in plan.CandidateDirectories)
        {
            var current = candidate;

            while (FileSystemPath.IsStrictDescendant(current, plan.CategoryRoot))
            {
                bool containsReparsePoint;

                try
                {
                    containsReparsePoint = FileSystemPath.ContainsReparsePoint(
                        fileSystem,
                        current,
                        plan.DownloadRoot);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Unable to inspect qBittorrent cleanup path {CleanupPath}", current);
                    break;
                }

                if (!FileSystemPath.IsSameOrDescendant(current, plan.DownloadRoot) || containsReparsePoint)
                {
                    logger.LogWarning("Skipping unsafe qBittorrent directory cleanup path {CleanupPath}", current);
                    break;
                }

                if (fileSystem.Directory.Exists(current))
                {
                    try
                    {
                        if (fileSystem.Directory.EnumerateFileSystemEntries(current).Any())
                        {
                            break;
                        }

                        fileSystem.Directory.Delete(current, false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Unable to remove empty qBittorrent job directory {CleanupPath}", current);
                        break;
                    }
                }

                var parent = fileSystem.Path.GetDirectoryName(current);

                if (string.IsNullOrWhiteSpace(parent) || FileSystemPath.PathsEqual(parent, current))
                {
                    break;
                }

                current = FileSystemPath.Normalize(parent);
            }
        }
    }

    private sealed record EmptyDirectoryCleanupPlan(
        string DownloadRoot,
        string CategoryRoot,
        IReadOnlyList<string> CandidateDirectories);

    private static Torrent CreateTorrent(string? category)
    {
        var defaults = Settings.Get.Downloads.Defaults;
        var normalizedCategory = NormalizeCategory(
            string.IsNullOrWhiteSpace(category) ? defaults.Category : category);

        return new()
        {
            Category = normalizedCategory,
            DownloadClient = Data.Enums.DownloadClient.Internal,
            HostDownloadAction = defaults.HostDownloadAction,
            FinishedAction = defaults.FinishedAction,
            FinishedActionDelay = defaults.FinishedActionDelay,
            DownloadMinSize = defaults.MinFileSize,
            IncludeRegex = defaults.IncludeRegex,
            ExcludeRegex = defaults.ExcludeRegex,
            TorrentRetryAttempts = defaults.TorrentRetryAttempts,
            DownloadRetryAttempts = defaults.DownloadRetryAttempts,
            DeleteOnError = defaults.DeleteOnError,
            Lifetime = defaults.TorrentLifetime,
            Priority = defaults.Priority > 0 ? defaults.Priority : null
        };
    }

    private static QBittorrentTorrentInfo MapTorrent(Torrent torrent)
    {
        var providerProgress = Math.Clamp((torrent.RdProgress ?? 0) / 100d, 0d, 1d);
        var localProgress = torrent.Downloads.Count == 0
            ? 0d
            : torrent.Downloads.Average(download =>
                download.Completed.HasValue
                    ? 1d
                    : download.BytesTotal > 0
                        ? Math.Clamp((double)download.BytesDone / download.BytesTotal, 0d, 1d)
                        : 0d);

        var completed = torrent.Completed.HasValue && string.IsNullOrWhiteSpace(torrent.Error);
        var progress = completed ? 1d : Math.Min(0.999d, (providerProgress + localProgress) / 2d);
        var localSpeed = torrent.Downloads.Sum(download => Math.Max(0, download.Speed));
        var downloadSpeed = torrent.Downloads.Count > 0 ? localSpeed : Math.Max(0, torrent.RdSpeed ?? 0);
        var activeDownloadSize = torrent.Downloads.Sum(download => Math.Max(0, download.BytesTotal));
        var size = Math.Max(0, torrent.RdSize ?? activeDownloadSize);
        var savePath = GetTorrentSavePath(torrent);

        return new()
        {
            Hash = torrent.Hash,
            Name = torrent.RdName ?? torrent.Hash,
            Category = torrent.Category ?? string.Empty,
            Progress = progress,
            State = GetState(torrent, completed, downloadSpeed),
            SavePath = savePath,
            ContentPath = GetContentPath(torrent, savePath),
            Size = size,
            DownloadSpeed = downloadSpeed,
            Eta = GetEta(completed, size, progress, downloadSpeed),
            // ADC does not seed. A zero ratio limit tells torrent-aware clients
            // that completed payloads are immediately eligible for post-import cleanup.
            RatioLimit = 0,
            LastActivity = (torrent.Completed ?? torrent.RdEnded ?? torrent.Added).ToUnixTimeSeconds()
        };
    }

    private static string GetState(Torrent torrent, bool completed, long downloadSpeed)
    {
        if (!string.IsNullOrWhiteSpace(torrent.Error) || torrent.RdStatus == TorrentStatus.Error)
        {
            return "error";
        }

        if (completed)
        {
            return "pausedUP";
        }

        if (downloadSpeed > 0)
        {
            return "downloading";
        }

        return torrent.RdStatus switch
        {
            TorrentStatus.Processing or TorrentStatus.WaitingForFileSelection => "metaDL",
            TorrentStatus.Downloading => "downloading",
            TorrentStatus.Uploading => "downloading",
            _ => "queuedDL"
        };
    }

    private static long GetEta(bool completed, long size, double progress, long downloadSpeed)
    {
        if (completed)
        {
            return 0;
        }

        if (size <= 0 || downloadSpeed <= 0 || progress <= 0)
        {
            return UnknownEta;
        }

        var eta = Math.Ceiling(size * (1d - progress) / downloadSpeed);
        return (long)Math.Clamp(eta, 0d, UnknownEta);
    }

    private static string GetSavePath(string? category)
    {
        var mappedPath = string.IsNullOrWhiteSpace(Settings.Get.Integrations.ReportedDownloadPath)
            ? Settings.Get.Storage.DownloadPath
            : Settings.Get.Integrations.ReportedDownloadPath;

        return CombineMappedPath(mappedPath, category);
    }

    private static string GetTorrentSavePath(Torrent torrent)
    {
        var settings = Settings.Get;
        var usesCurrentLocalPath = string.IsNullOrWhiteSpace(torrent.LocalDownloadPath) ||
                                   FileSystemPath.PathsEqual(
                                       torrent.LocalDownloadPath,
                                       settings.Storage.DownloadPath);
        var mappedPath = usesCurrentLocalPath
            ? string.IsNullOrWhiteSpace(settings.Integrations.ReportedDownloadPath)
                ? settings.Storage.DownloadPath
                : settings.Integrations.ReportedDownloadPath
            : string.IsNullOrWhiteSpace(torrent.ClientReportedDownloadPath)
                ? torrent.LocalDownloadPath!
                : torrent.ClientReportedDownloadPath;

        return CombineMappedPath(mappedPath, torrent.Category);
    }

    private static string GetContentPath(Torrent torrent, string savePath)
    {
        if (torrent.Downloads.Count == 1)
        {
            var download = torrent.Downloads[0];
            var relativePath = DownloadHelper.IsSupportedArchive(download)
                ? null
                : DownloadHelper.GetDownloadPath(torrent, download);

            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                return CombineMappedPath(savePath, relativePath);
            }
        }

        return string.IsNullOrWhiteSpace(torrent.RdName)
            ? savePath
            : CombineMappedPath(savePath, DownloadHelper.GetTorrentDirectoryName(torrent));
    }

    private static string CombineMappedPath(string root, string? child)
    {
        var separator = root.Contains('\\') && !root.Contains('/')
            ? '\\'
            : root.Contains('/') && !root.Contains('\\')
                ? '/'
                : Path.DirectorySeparatorChar;

        var trimmedRoot = root.Trim();
        var result = trimmedRoot.TrimEnd('/', '\\');

        if (result.Length == 2 &&
            result[1] == ':' &&
            trimmedRoot.Length > result.Length &&
            trimmedRoot[result.Length] is '/' or '\\')
        {
            result += separator;
        }

        var rootOnly = result.Length == 0 && root.IndexOfAny(['/', '\\']) >= 0;

        if (rootOnly)
        {
            result = separator.ToString();
        }

        if (string.IsNullOrWhiteSpace(child))
        {
            return result;
        }

        var normalizedChild = child.Trim().Trim('/', '\\')
                                   .Replace('/', separator)
                                   .Replace('\\', separator);

        if (result.EndsWith(separator))
        {
            return result + normalizedChild;
        }

        return result.Length == 0 ? normalizedChild : $"{result}{separator}{normalizedChild}";
    }
}
