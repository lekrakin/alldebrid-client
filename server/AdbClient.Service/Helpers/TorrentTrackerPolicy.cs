using MonoTorrent;

namespace AdbClient.Service.Helpers;

internal static class TorrentTrackerPolicy
{
    public static void EnsureAllowed(MagnetLink magnet, string? blockedTrackerList)
    {
        var blockedTrackers = ParseBlockedTrackers(blockedTrackerList);

        if (blockedTrackers.Count == 0 || magnet.AnnounceUrls == null)
        {
            return;
        }

        var blockedUrls = magnet.AnnounceUrls
            .Where(url => IsBlocked(url, blockedTrackers))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (blockedUrls.Count > 0)
        {
            throw new InvalidDataException(
                $"Cannot add torrent because it contains blocked trackers ({blockedUrls.Count}).");
        }
    }

    public static void EnsureAllowed(MonoTorrent.Torrent torrent, string? blockedTrackerList)
    {
        var blockedTrackers = ParseBlockedTrackers(blockedTrackerList);

        if (blockedTrackers.Count == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(torrent.Source) && IsBlocked(torrent.Source, blockedTrackers))
        {
            throw new InvalidDataException(
                "Cannot add torrent because its source matches the blocked tracker policy.");
        }

        var announceUrls = torrent.AnnounceUrls ?? [];
        var blockedUrls = announceUrls.SelectMany(tier => tier)
            .Where(url => IsBlocked(url, blockedTrackers))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (blockedUrls.Count > 0)
        {
            throw new InvalidDataException(
                $"Cannot add torrent because it contains blocked trackers ({blockedUrls.Count}).");
        }
    }

    private static IReadOnlyList<string> ParseBlockedTrackers(string? blockedTrackerList)
    {
        if (string.IsNullOrWhiteSpace(blockedTrackerList))
        {
            return [];
        }

        return blockedTrackerList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tracker => tracker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsBlocked(string value, IReadOnlyList<string> blockedTrackers)
    {
        return blockedTrackers.Any(blockedTracker =>
            value.Contains(blockedTracker, StringComparison.OrdinalIgnoreCase));
    }
}
