using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using AdbClient.Data.Enums;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

namespace AdbClient.Data.Models.Internal;

public class DbSettings
{
    [DisplayName("General")]
    [Description("Application logging, access control, and update notifications.")]
    public DbSettingsGeneral General { get; set; } = new();

    [DisplayName("Downloads")]
    [Description("Host transfer limits and defaults inherited by every newly added torrent.")]
    public DbSettingsDownloads Downloads { get; set; } = new();

    [DisplayName("Storage")]
    [Description("The physical location where downloaded files are written.")]
    public DbSettingsStorage Storage { get; set; } = new();

    [DisplayName("AllDebrid")]
    [Description("Provider connection, synchronization, queueing, and tracker safeguards.")]
    public DbSettingsProvider Provider { get; set; } = new();

    [DisplayName("Integrations")]
    [Description("qBittorrent-compatible clients and optional completion/export hooks.")]
    public DbSettingsIntegrations Integrations { get; set; } = new();

    [DisplayName("Watch Folder")]
    [Description("Optional automatic import of .torrent and .magnet files from a local folder.")]
    public DbSettingsWatchFolder WatchFolder { get; set; } = new();
}

public class DbSettingsGeneral
{
    [DisplayName("Log level")]
    [Description("Warning for normal use; Debug for diagnosing issues.")]
    public LogLevel LogLevel { get; set; } = LogLevel.Warning;

    [DisplayName("Authentication")]
    [Description("WARNING: No Authentication allows access to anyone who can reach this application.")]
    public AuthenticationType AuthenticationType { get; set; } = AuthenticationType.None;

    [DisplayName("Disable update notifications")]
    [Description("Hide notifications when a newer stable release is available.")]
    public bool DisableUpdateNotifications { get; set; }
}

public class DbSettingsDownloads
{
    [DisplayName("Concurrent file downloads")]
    [Description("Maximum files transferred from AllDebrid to this host at the same time.")]
    [Range(1, int.MaxValue)]
    public int ConcurrentFiles { get; set; } = 2;

    [DisplayName("Concurrent extractions")]
    [Description("Maximum archive extractions at the same time. 0 disables extraction.")]
    [Range(0, int.MaxValue)]
    public int ConcurrentExtractions { get; set; } = 1;

    [DisplayName("Total speed limit (MB/s)")]
    [Description("Combined transfer limit across active downloads. 0 = unlimited.")]
    [Range(0, int.MaxValue)]
    public int SpeedLimit { get; set; }

    [DisplayName("Connections per file")]
    [Description("Parallel connections used for each file (maximum 16). 0 or 1 uses one connection.")]
    [Range(0, 16)]
    public int ConnectionsPerFile { get; set; } = 8;

    [DisplayName("Chunks per file")]
    [Description("Chunks used to split each file (maximum 128). 0 = automatic (8).")]
    [Range(0, 128)]
    public int ChunksPerFile { get; set; }

    [DisplayName("New torrent defaults")]
    [Description("Applied when a torrent is added through the web UI, watch folder, provider sync, or qBittorrent API.")]
    public DbSettingsTorrentDefaults Defaults { get; set; } = new();
}

public class DbSettingsTorrentDefaults : DbSettingsDownloadRules
{
    [DisplayName("Host download action")]
    [Description("Choose whether completed provider files are downloaded to this host.")]
    public TorrentHostDownloadAction HostDownloadAction { get; set; }

    [DisplayName("Category")]
    [Description("Default category when the source does not provide one. Categories become subfolders of the local download path.")]
    public string? Category { get; set; }

    [DisplayName("Completed record action")]
    [Description("Records are retained by default. This controls AllDebrid Client and provider records after local files are saved; it never deletes local files.")]
    public TorrentFinishedAction FinishedAction { get; set; } = TorrentFinishedAction.None;

    [DisplayName("Completed action delay (minutes)")]
    [Description("Minutes to wait before applying the completed record action.")]
    [Range(0, int.MaxValue)]
    public int FinishedActionDelay { get; set; }
}

public class DbSettingsDownloadRules
{
    [DisplayName("Minimum file size (MB)")]
    [Description("Skip files at or below this size. 0 = download all. A small value can exclude artwork and metadata files.")]
    [Range(0, int.MaxValue)]
    public int MinFileSize { get; set; }

    [DisplayName("Include files (regex)")]
    [Description("Only download paths matching this regular expression. When set, this takes precedence over Exclude files.")]
    public string? IncludeRegex { get; set; }

    [DisplayName("Exclude files (regex)")]
    [Description("Skip paths matching this regular expression. Ignored when Include files is set.")]
    public string? ExcludeRegex { get; set; }

    [DisplayName("Torrent retry attempts")]
    [Description("Times to retry the full torrent after repeated file or provider failures.")]
    [Range(0, 1000)]
    public int TorrentRetryAttempts { get; set; } = 1;

    [DisplayName("File retry attempts")]
    [Description("Times to retry a failed file transfer before the torrent is retried.")]
    [Range(0, 1000)]
    public int DownloadRetryAttempts { get; set; } = 3;

    [DisplayName("Delete errors after (minutes)")]
    [Description("Delete failed provider, client, and local data after this delay. 0 disables automatic deletion.")]
    [Range(0, 1000)]
    public int DeleteOnError { get; set; }

    [DisplayName("Maximum pending lifetime (minutes)")]
    [Description("Mark a torrent as failed if it remains pending for this long. Completed torrents are unaffected. 0 disables the limit.")]
    [Range(0, 100000)]
    public int TorrentLifetime { get; set; }

    [DisplayName("Priority")]
    [Description("Default queue priority. 0 uses normal first-in, first-out ordering; positive values run first, with lower values taking precedence.")]
    [Range(0, int.MaxValue)]
    public int Priority { get; set; }
}

public class DbSettingsStorage
{
    [DisplayName("Local download path")]
    [Description("Physical directory where AllDebrid Client writes downloaded files. Categories are created beneath it.")]
    [Required(AllowEmptyStrings = false)]
    public string DownloadPath { get; set; } = GetDefaultDownloadPath();

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
    [DisplayName("API key")]
    [Description(@"Create or copy an AllDebrid API key here:
<a href=""https://alldebrid.com/apikeys/"" target=""_blank"" rel=""noopener"">https://alldebrid.com/apikeys/</a>")]
    [DataType(DataType.Password)]
    public string ApiKey { get; set; } = "";

    [DisplayName("Connection timeout (seconds)")]
    [Description("Time to wait for an AllDebrid request before treating it as failed.")]
    [Range(1, int.MaxValue)]
    public int Timeout { get; set; } = 10;

    [DisplayName("Status check interval (seconds)")]
    [Description("Provider polling interval while the UI is connected. Minimum 5 seconds; without a UI connection, polling is less frequent (three times this interval, minimum 30 seconds).")]
    [Range(5, int.MaxValue)]
    public int CheckInterval { get; set; } = 10;

    [DisplayName("Import provider torrents")]
    [Description("Discover torrents added directly to AllDebrid and import them into this client.")]
    public bool AutoImport { get; set; }

    [DisplayName("Remove missing provider records")]
    [Description("Remove a client record when its matching AllDebrid torrent disappears. Local files are never deleted by this setting.")]
    public bool AutoDelete { get; set; }

    [DisplayName("Concurrent provider torrents")]
    [Description("Maximum torrents submitted to AllDebrid at the same time. 0 = unlimited.")]
    [Range(0, int.MaxValue)]
    public int ConcurrentTorrents { get; set; }

    [DisplayName("Tracker enrichment URL")]
    [Description("Optional HTTP(S) tracker list appended to magnet links and torrent files before submission.")]
    public string? TrackerEnrichmentList { get; set; }

    [DisplayName("Tracker list cache (minutes)")]
    [Description("How long to cache the tracker list. 0 disables caching.")]
    [Range(0, int.MaxValue)]
    public int TrackerEnrichmentCacheExpiration { get; set; } = 60;

    [DisplayName("Blocked trackers")]
    [Description("Comma-separated tracker keywords rejected before submission to guard against private-tracker leaks.")]
    public string? BannedTrackers { get; set; }
}

public class DbSettingsIntegrations
{
    [DisplayName("qBittorrent categories")]
    [Description("Comma-separated categories exposed through the qBittorrent API. Sonarr, Radarr, and Logpose create their categories automatically.")]
    public string? Categories { get; set; }

    [DisplayName("Client-visible download path")]
    [Description("Advanced: path reported through the qBittorrent API when another application sees the download folder at a different location. Leave blank to report the local path.")]
    public string? ReportedDownloadPath { get; set; }

    [DisplayName("Copy added torrent metadata to")]
    [Description("Optional folder that receives a .magnet or .torrent copy whenever a torrent is added. Leave blank unless another tool consumes these files.")]
    public string? AddedTorrentCopyPath { get; set; }

    [DisplayName("Completion command")]
    [Description("Optional program executed after all selected files have been saved.")]
    public DbSettingsCompletionCommand CompletionCommand { get; set; } = new();
}

public class DbSettingsCompletionCommand
{
    [DisplayName("Executable path")]
    [Description("Full path to the executable. Leave blank to disable the completion command.")]
    public string? ExecutablePath { get; set; }

    [DisplayName("Arguments")]
    [Description("%N: Torrent name  %L: Category  %F: Content path\n%R: Category root  %D: Torrent path  %C: File count\n%Z: Size (bytes)  %I: Info hash")]
    public string? Arguments { get; set; }

    [DisplayName("Timeout (seconds)")]
    [Description("Maximum time the completion command may run before its process tree is terminated.")]
    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 60;
}

public class DbSettingsWatchFolder
{
    [DisplayName("Inbox path")]
    [Description("Folder scanned for .torrent and .magnet files. Leave blank to disable watch-folder imports.")]
    public string? InboxPath { get; set; }

    [DisplayName("Processed path")]
    [Description("Successful inputs are moved here. Leave blank to use a processed folder inside the inbox.")]
    public string? ProcessedPath { get; set; }

    [DisplayName("Error path")]
    [Description("Failed inputs are moved here. Leave blank to use an error folder inside the inbox.")]
    public string? ErrorPath { get; set; }

    [DisplayName("Scan interval (seconds)")]
    [Description("Seconds between inbox scans.")]
    [Range(1, int.MaxValue)]
    public int Interval { get; set; } = 60;
}
