namespace AdbClient.Data.Models.TorrentClient;

public class TorrentClientTorrent
{
    public string Id { get; set; } = default!;
    public string Filename { get; set; } = default!;
    public string? OriginalFilename { get; set; }
    public string Hash { get; set; } = default!;
    public long Bytes { get; set; }
    public long OriginalBytes { get; set; }
    public string? Host { get; set; }
    public long Split { get; set; }
    public long Progress { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public long StatusCode { get; set; }
    public DateTimeOffset? Added { get; set; }
    public List<TorrentClientFile>? Files { get; set; }
    public List<string>? Links { get; set; }
    public DateTimeOffset? Ended { get; set; }
    public long? Speed { get; set; }
}
