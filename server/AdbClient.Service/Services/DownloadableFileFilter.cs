using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using AdbClient.Data.Helpers;
using AdbClient.Data.Models.Data;

namespace AdbClient.Service.Services;

public interface IDownloadableFileFilter
{
    public bool IsDownloadable(Torrent torrent, string filePath, long fileSize);
}

public class DownloadableFileFilter(ILogger<DownloadableFileFilter> logger) : IDownloadableFileFilter
{
    public bool IsDownloadable(Torrent torrent, string filePath, long fileSize)
    {
        var isDownloadable = PassesSizeFilter(torrent, filePath, fileSize) &&
                             PassesFilePathFilter(torrent, filePath);

        if (isDownloadable)
        {
            logger.LogDebug("File {filePath} was included after filtering", filePath);
        }

        return isDownloadable;
    }

    private bool PassesSizeFilter(Torrent torrent, string filePath, long fileSize)
    {
        if (torrent.DownloadMinSize <= 0 || fileSize > torrent.DownloadMinSize * 1024L * 1024L)
        {
            return true;
        }

        logger.LogDebug("Not downloading file {filePath} file size {fileSize} smaller than minimum {downloadMinSize}", filePath, fileSize, torrent.DownloadMinSize);

        return false;
    }

    private bool PassesFilePathFilter(Torrent torrent, string filePath)
    {
        return PassesIncludeRegexFilter(torrent, filePath) && PassesExcludeRegexFilter(torrent, filePath);
    }

    private bool PassesIncludeRegexFilter(Torrent torrent, string filePath)
    {
        if (string.IsNullOrWhiteSpace(torrent.IncludeRegex))
        {
            return true;
        }

        if (!TryIsMatch(filePath, torrent.IncludeRegex, "include", out var isMatch))
        {
            return false;
        }

        if (isMatch)
        {
            return true;
        }

        logger.LogDebug("Not downloading file {filePath} does not match regex {includeRegex}", filePath, torrent.IncludeRegex);

        return false;
    }

    private bool PassesExcludeRegexFilter(Torrent torrent, string filePath)
    {
        // If the IncludeRegex is set, ignore the ExcludeRegex 
        if (!string.IsNullOrWhiteSpace(torrent.IncludeRegex))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(torrent.ExcludeRegex))
        {
            return true;
        }

        if (!TryIsMatch(filePath, torrent.ExcludeRegex, "exclude", out var isMatch))
        {
            return false;
        }

        if (!isMatch)
        {
            return true;
        }

        logger.LogDebug("Not downloading file {filePath} matches regex {excludeRegex}", filePath, torrent.ExcludeRegex);

        return false;
    }

    private bool TryIsMatch(string filePath, string pattern, string filterName, out bool isMatch)
    {
        try
        {
            isMatch = BoundedRegex.IsMatch(filePath, pattern);
            return true;
        }
        catch (RegexMatchTimeoutException ex)
        {
            logger.LogWarning(
                ex,
                "Not downloading file {FilePath}: the {FilterName} regular expression exceeded the {RegexTimeout} safety limit",
                filePath,
                filterName,
                BoundedRegex.MatchTimeout);
            isMatch = false;
            return false;
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(
                ex,
                "Not downloading file {FilePath}: the {FilterName} regular expression is invalid",
                filePath,
                filterName);
            isMatch = false;
            return false;
        }
    }
}
