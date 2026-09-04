using AdbClient.Data.Models.Data;

namespace AdbClient.Service.Helpers;

public static class Logger
{
    public static string ToLog(this Download download)
    {
        var source = DescribeDownloadSource(download);

        var done = download.BytesTotal > 0
            ? (int)Math.Clamp((double)download.BytesDone / download.BytesTotal * 100, 0, 100)
            : 0;

        return $"for {source}. Completed: {done}%, avg speed: {download.Speed}bytes/s ({download.DownloadId}) remoteID: {download.RemoteId}";
    }

    internal static string DescribeDownloadSource(string? url, string? knownFileName = null)
    {
        var fileName = SanitizeFileName(knownFileName);
        string? host = null;

        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                host = SanitizeLogComponent(uri.IdnHost);

                if (!uri.IsDefaultPort && host != null)
                {
                    host = $"{host}:{uri.Port}";
                }
            }
        }
        catch (UriFormatException)
        {
            // Malformed provider values must not break error reporting.
        }

        return (fileName, host) switch
        {
            (not null, not null) => $"download '{fileName}' from host '{host}'",
            (not null, null) => $"download '{fileName}'",
            (null, not null) => $"download from host '{host}'",
            _ => "download from an unknown source"
        };
    }

    internal static string DescribeDownloadSource(Download download)
    {
        var sourceUrl = string.IsNullOrWhiteSpace(download.Link) ? download.Path : download.Link;
        return DescribeDownloadSource(sourceUrl, download.FileName);
    }

    internal static string DescribeDownloadFailure(Exception exception, Download download)
    {
        var sourceUrl = string.IsNullOrWhiteSpace(download.Link) ? download.Path : download.Link;
        return DescribeDownloadFailure(exception, sourceUrl, download.FileName);
    }

    internal static string DescribeDownloadFailure(Exception exception, string? sourceUrl, string? knownFileName)
    {
        var status = exception is HttpRequestException { StatusCode: { } statusCode }
            ? $" (HTTP {(int)statusCode} {statusCode})"
            : string.Empty;

        // Exception messages and inner exceptions may contain signed URLs or credentials.
        return $"{exception.GetType().Name}{status} (HRESULT 0x{exception.HResult:X8}) for {DescribeDownloadSource(sourceUrl, knownFileName)}.";
    }

    public static string ToLog(this Torrent torrent)
    {
        return $"for torrent {torrent.RdName} ({torrent.RdId} - {torrent.RdStatusRaw} {torrent.RdProgress}%) ({torrent.TorrentId})";
    }

    private static string? SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var withoutQuery = value.Split(['?', '#'], 2)[0].Replace('\\', '/');
        var fileName = SanitizeLogComponent(withoutQuery[(withoutQuery.LastIndexOf('/') + 1)..]);

        return fileName is null or "." or ".."
            ? null
            : fileName[..Math.Min(fileName.Length, 255)];
    }

    private static string? SanitizeLogComponent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return sanitized.Length == 0 ? null : sanitized;
    }
}
