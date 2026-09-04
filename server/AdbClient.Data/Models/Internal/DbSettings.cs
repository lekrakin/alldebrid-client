using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using AdbClient.Data.Enums;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

namespace AdbClient.Data.Models.Internal;

public class DbSettings
{
    [DisplayName("General")]
    [Description("")]
    public DbSettingsGeneral General { get; set; } = new();

    [DisplayName("Download")]
    [Description("")]
    public DbSettingsDownloadClient DownloadClient { get; set; } = new();

    [DisplayName("Paths")]
    [Description("")]
    public DbSettingsPaths Paths { get; set; } = new();

    [DisplayName("AllDebrid")]
    [Description("")]
    public DbSettingsProvider Provider { get; set; } = new();

    [DisplayName("Watch")]
    [Description("")]
    public DbSettingsWatch Watch { get; set; } = new();
}

public class DbSettingsGeneral
{
    [DisplayName("Log level")]
    [Description("Warning for normal use; Debug for diagnosing issues.")]
    public LogLevel LogLevel { get; set; } = LogLevel.Error;

    [DisplayName("Maximum parallel downloads")]
    [Description("Max simultaneous active downloads to your host.")]
    [Range(1, int.MaxValue)]
    public int DownloadLimit { get; set; } = 2;

    [DisplayName("Maximum unpack processes")]
    [Description("Max simultaneous extractions. 0 disables unpacking.")]
    [Range(0, int.MaxValue)]
    public int UnpackLimit { get; set; } = 1;

    [DisplayName("Categories")]
    [Description("Comma-separated list of available categories.")]
    public string? Categories { get; set; } = null;

    [DisplayName("Run external program on torrent completion")]
    [Description("Full path to the executable to run on completion. No arguments here.")]
    public string? RunOnTorrentCompleteFileName { get; set; } = null;

    [DisplayName("External program arguments")]
    [Description("Arguments passed to the executable above.\n%N: Torrent name  %L: Category  %F: Content path\n%R: Root path  %D: Save path  %C: File count\n%Z: Size (bytes)  %I: Info hash")]
    public string? RunOnTorrentCompleteArguments { get; set; } = null;

    [DisplayName("Authentication Type")]
    [Description("WARNING: None allows unauthenticated access to anyone who can reach the URL.")]
    public AuthenticationType AuthenticationType { get; set; } = AuthenticationType.None;

    [DisplayName("Tracker enrichment list")]
    [Description("URL of a tracker list to append to magnet links and torrent files.")]
    public string? TrackerEnrichmentList { get; set; } = null;

    [DisplayName("Tracker enrichment cache expiration")]
    [Description("Minutes to cache the tracker list. 0 disables caching.")]
    [Range(0, int.MaxValue)]
    public int TrackerEnrichmentCacheExpiration { get; set; } = 60;

    [DisplayName("Banned Trackers")]
    [Description("Comma-separated tracker keywords to block. Guards against private tracker leaks.")]
    public string? BannedTrackers { get; set; } = null;

    [DisplayName("Disable update notifications")]
    [Description("Hide notifications when a newer release is available.")]
    public bool DisableUpdateNotifications { get; set; } = false;
}

public class DbSettingsDownloadClient
{
    [DisplayName("Download speed (MB/s)")]
    [Description("Max download speed in MB/s. 0 = unlimited. Internal downloader only.")]
    [Range(0, int.MaxValue)]
    public int MaxSpeed { get; set; } = 0;

    [DisplayName("Parallel connections per download")]
    [Description("Connections per file (maximum 16). 0 or 1 uses one connection. Internal downloader only.")]
    [Range(0, 16)]
    public int ParallelCount { get; set; } = 8;

    [DisplayName("Parallel chunks per download")]
    [Description("Number of chunks used to split each download (maximum 128). 0 = default (8). Internal downloader only.")]
    [Range(0, 128)]
    public int ParallelChunkCount { get; set; } = 0;

    [DisplayName("Auto-import from provider")]
    [Description("Import torrents added directly to your debrid provider, not via this client.")]
    public bool AutoImport { get; set; } = false;

    [DisplayName("Auto-delete removed from provider")]
    [Description("Delete from this client when removed from the debrid provider.")]
    public bool AutoDelete { get; set; } = false;

    [DisplayName("Max parallel downloads")]
    [Description("Max torrents queued for provider download at once. 0 = no limit.")]
    [Range(0, int.MaxValue)]
    public int MaxParallelDownloads { get; set; } = 0;

    [DisplayName("Defaults")]
    public DbSettingsDefaultsWithCategory Default { get; set; } = new();
}

public class DbSettingsPaths
{
    [DisplayName("Local download path")]
    [Description("Physical directory where AllDebrid Client writes downloaded files.")]
    [Required(AllowEmptyStrings = false)]
    public string DownloadPath { get; set; } = GetDefaultDownloadPath();

    [DisplayName("Client-visible download path (advanced)")]
    [Description("Usually leave blank. Set only when an integrated application sees the download directory at a different path, such as across a container boundary.")]
    public string? MappedPath { get; set; }

    [DisplayName("Copy added torrent files")]
    [Description("Copy each added torrent or magnet file to this directory.")]
    public string? CopyAddedTorrents { get; set; } = null;

    [DisplayName("Watch path")]
    [Description("Folder to watch for .torrent and .magnet files to auto-import.")]
    public string? WatchPath { get; set; } = null;

    [DisplayName("Watch error path")]
    [Description(@"Failed torrents are moved here. Defaults to \error inside the watch folder.")]
    public string? WatchErrorPath { get; set; } = null;

    [DisplayName("Watch processed path")]
    [Description(@"Successful torrents are moved here. Defaults to \processed inside the watch folder.")]
    public string? WatchProcessedPath { get; set; } = null;

    private static string GetDefaultDownloadPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "/data/downloads";
        }

        var commonDataPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(commonDataPath, "AllDebridClient", "downloads");
    }
}

public class DbSettingsProvider
{
    [DisplayName("API Key")]
    [Description(@"You can find your AllDebrid API key here:
<a href=""https://alldebrid.com/apikeys/"" target=""_blank"" rel=""noopener"">https://alldebrid.com/apikeys/</a>")]
    [DataType(DataType.Password)]
    public string ApiKey { get; set; } = "";

    [DisplayName("Connection timeout (seconds)")]
    [Description("Seconds before a provider connection times out. Increase if you see timeout errors in logs.")]
    [Range(1, int.MaxValue)]
    public int Timeout { get; set; } = 10;

    [DisplayName("Check interval (seconds)")]
    [Description("Seconds between provider API status checks. Minimum 5; tripled when there are no active downloads.")]
    [Range(5, int.MaxValue)]
    public int CheckInterval { get; set; } = 10;
}

public class DbSettingsWatch
{
    [DisplayName("Check interval (seconds)")]
    [Description("Seconds between watch folder scans.")]
    [Range(1, int.MaxValue)]
    public int Interval { get; set; } = 60;
}

public class DbSettingsDefaultsWithCategory : DbSettingsDefaults
{
    [DisplayName("Post torrent download action")]
    [Description("What to do after the provider finishes downloading. Use 'Don't download' to store on the provider only.")]
    public TorrentHostDownloadAction HostDownloadAction { get; set; }

    [DisplayName("Category")]
    [Description("Default category for watch folder imports.")]
    public string? Category { get; set; } = null;

    [DisplayName("Completed record action")]
    [Description("Controls which AllDebrid Client and provider records are removed after files are saved. External qBittorrent clients can still request removal of successfully imported local files.")]
    public TorrentFinishedAction FinishedAction { get; set; } = TorrentFinishedAction.RemoveAllTorrents;

    [DisplayName("Finished action delay (minutes)")]
    [Description("Minutes to wait before running the finished action.")]
    [Range(0, int.MaxValue)]
    public int FinishedActionDelay { get; set; } = 0;
}

public class DbSettingsDefaults
{
    [DisplayName("Only download available files")]
    [Description("Skip files the provider hasn't cached yet.")]
    public bool OnlyDownloadAvailableFiles { get; set; } = true;

    [DisplayName("Minimum file size (MB)")]
    [Description("Skip files at or below this size in MB. 0 = download all. Set a small value when using *arr to exclude artwork and metadata files.")]
    [Range(0, int.MaxValue)]
    public int MinFileSize { get; set; } = 0;

    [DisplayName("Include files (regex)")]
    [Description("Only download files matching this regex. Use either Include or Exclude — not both.")]
    public string? IncludeRegex { get; set; }

    [DisplayName("Exclude files (regex)")]
    [Description("Skip files matching this regex. Use either Exclude or Include — not both.")]
    public string? ExcludeRegex { get; set; }

    [DisplayName("Torrent retry attempts")]
    [Description("Times to retry the full torrent after repeated download failures.")]
    [Range(0, 1000)]
    public int TorrentRetryAttempts { get; set; } = 1;

    [DisplayName("Download retry attempts")]
    [Description("Times to retry a single failed download.")]
    [Range(0, 1000)]
    public int DownloadRetryAttempts { get; set; } = 3;

    [DisplayName("Delete on error (minutes)")]
    [Description("Delete from provider and client after this many minutes in error state. 0 to disable.")]
    [Range(0, 1000)]
    public int DeleteOnError { get; set; } = 0;

    [DisplayName("Torrent lifetime (minutes)")]
    [Description("Max age before marking as error. Ignored once downloads are complete. 0 to disable.")]
    [Range(0, 100000)]
    public int TorrentLifetime { get; set; } = 0;

    [DisplayName("Priority")]
    [Description("Download priority (1 = highest). 0 = disabled.")]
    [Range(0, int.MaxValue)]
    public int Priority { get; set; } = 0;
}
