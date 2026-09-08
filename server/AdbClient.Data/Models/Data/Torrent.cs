using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdbClient.Data.Enums;
using AdbClient.Data.Models.TorrentClient;

namespace AdbClient.Data.Models.Data;

public class Torrent
{
    [Key]
    public Guid TorrentId { get; set; }

    public string Hash { get; set; } = null!;

    public string? Category { get; set; }

    [JsonIgnore]
    public string? LocalDownloadPath { get; set; }

    [JsonIgnore]
    public string? ClientReportedDownloadPath { get; set; }

    [JsonIgnore]
    public bool QbittorrentHidden { get; set; }

    [NotMapped]
    public bool ExternalClientRemoved => QbittorrentHidden;

    public TorrentDownloadAction DownloadAction { get; set; }
    public TorrentFinishedAction FinishedAction { get; set; }
    public int FinishedActionDelay { get; set; }
    public TorrentHostDownloadAction HostDownloadAction { get; set; }
    public int DownloadMinSize { get; set; }
    public string? IncludeRegex { get; set; }
    public string? ExcludeRegex { get; set; }
    public string? DownloadManualFiles { get; set; }
    public DownloadClient DownloadClient { get; set; }

    public DateTimeOffset Added { get; set; }
    public DateTimeOffset? FilesSelected { get; set; }
    public DateTimeOffset? Completed { get; set; }
    public DateTimeOffset? Retry { get; set; }

    public string? FileOrMagnet { get; set; }
    public bool IsFile { get; set; }

    public int? Priority { get; set; }
    public int RetryCount { get; set; }
    public int DownloadRetryAttempts { get; set; }
    public int TorrentRetryAttempts { get; set; }
    public int DeleteOnError { get; set; }
    public int Lifetime { get; set; }

    public string? Error { get; set; }

    [InverseProperty("Torrent")]
    public IList<Download> Downloads { get; set; } = [];

    public Provider? ClientKind { get; set; }
    public string? RdId { get; set; }
    public string? RdName { get; set; }
    public long? RdSize { get; set; }
    public string? RdHost { get; set; }
    public long? RdSplit { get; set; }
    public long? RdProgress { get; set; }
    public TorrentStatus? RdStatus { get; set; }
    public string? RdStatusRaw { get; set; }
    public DateTimeOffset? RdAdded { get; set; }
    public DateTimeOffset? RdEnded { get; set; }
    public long? RdSpeed { get; set; }

    // Retained for database compatibility with earlier versions.
    [JsonIgnore]
    public long? RdSeeders { get; set; }
    public string? RdFiles { get; set; }

    [NotMapped]
    public IList<TorrentClientFile> Files
    {
        get
        {
            if (string.IsNullOrWhiteSpace(RdFiles))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<List<TorrentClientFile>>(RdFiles) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    [NotMapped]
    public IList<string> ManualFiles
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DownloadManualFiles))
            {
                return [];
            }

            return DownloadManualFiles.Split(",");
        }
    }
}
