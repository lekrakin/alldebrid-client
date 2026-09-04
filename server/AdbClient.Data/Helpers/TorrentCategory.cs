namespace AdbClient.Data.Helpers;

public static class TorrentCategory
{
    public static string? Normalize(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        var normalized = category.Trim();
        var segments = normalized.Split('/');

        if (normalized.Contains('\\') ||
            segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or "..") ||
            normalized.Any(character => character < ' ' || "<>,:\"|?*".Contains(character)))
        {
            throw new ArgumentException($"Invalid torrent category: {category}", nameof(category));
        }

        return normalized;
    }
}
