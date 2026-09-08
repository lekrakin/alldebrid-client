using System.IO.Abstractions;
using AdbClient.Data.Models.Data;
using System.Web;

namespace AdbClient.Service.Helpers;

public static class DownloadHelper
{
    public static string GetCategoryPath(
        string downloadPath,
        string? category,
        IFileSystem? fileSystem = null)
    {
        if (string.IsNullOrWhiteSpace(downloadPath))
        {
            throw new InvalidDataException("The configured download directory is empty.");
        }

        fileSystem ??= new FileSystem();
        var downloadRoot = FileSystemPath.Normalize(downloadPath);
        var categoryPath = string.IsNullOrWhiteSpace(category)
            ? downloadRoot
            : FileSystemPath.Normalize(fileSystem.Path.Combine(downloadRoot, category));

        if (!FileSystemPath.IsSameOrDescendant(categoryPath, downloadRoot) ||
            FileSystemPath.ContainsReparsePoint(fileSystem, categoryPath, downloadRoot))
        {
            throw new InvalidDataException("Torrent category path is outside the configured download directory or contains a reparse point.");
        }

        return categoryPath;
    }

    public static string? GetDownloadPath(string downloadPath, Torrent torrent, Download download, IFileSystem? fileSystem = null)
    {
        if (string.IsNullOrWhiteSpace(download.Link) || torrent.RdName == null)
        {
            return null;
        }

        var fileName = GetFileName(download);

        if (fileName == null)
        {
            return null;
        }

        fileSystem ??= new FileSystem();
        var relativePath = GetRelativeDownloadPath(torrent, fileName);
        var filePath = fileSystem.Path.Combine(downloadPath, relativePath);
        var normalizedDownloadPath = FileSystemPath.Normalize(downloadPath);
        var normalizedFilePath = FileSystemPath.Normalize(filePath);

        if (!FileSystemPath.IsStrictDescendant(normalizedFilePath, normalizedDownloadPath))
        {
            throw new InvalidDataException("Torrent download path is outside the configured download directory.");
        }

        var directoryPath = fileSystem.Path.GetDirectoryName(filePath)
                            ?? throw new InvalidDataException("Torrent download path has no parent directory.");
        var normalizedDirectoryPath = FileSystemPath.Normalize(directoryPath);

        if (FileSystemPath.ContainsReparsePoint(fileSystem, normalizedDirectoryPath, normalizedDownloadPath))
        {
            throw new InvalidDataException("Torrent download path contains a reparse point.");
        }

        if (!fileSystem.Directory.Exists(directoryPath))
        {
            fileSystem.Directory.CreateDirectory(directoryPath);
        }

        return filePath;
    }

    public static string? GetDownloadPath(Torrent torrent, Download download)
    {
        if (torrent.RdName == null)
        {
            return null;
        }

        var fileName = GetFileName(download);
        return fileName == null ? null : GetRelativeDownloadPath(torrent, fileName);
    }

    private static string? FindSubPath(Torrent torrent, string fileName)
    {
        var matchSegments = torrent.Files
                                   .Where(file => !string.IsNullOrWhiteSpace(file.Path))
                                   .Select(file => file.Path.Split(
                                       ['/', '\\'],
                                       StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                   .FirstOrDefault(segments => segments.Length > 0 &&
                                                               string.Equals(
                                                                   FileHelper.RemoveInvalidFileNameChars(segments[^1]),
                                                                   fileName,
                                                                   StringComparison.OrdinalIgnoreCase));

        if (matchSegments == null || matchSegments.Length <= 1)
        {
            return null;
        }

        return Path.Combine(matchSegments[..^1]
                           .Select(FileHelper.RemoveInvalidFileNameChars)
                           .ToArray());
    }

    public static string? GetFileName(Download download)
    {
        var fileName = download.FileName;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            if (string.IsNullOrWhiteSpace(download.Link))
            {
                return null;
            }

            fileName = HttpUtility.UrlDecode(new Uri(download.Link).Segments.Last());
        }

        var sanitizedFileName = FileHelper.RemoveInvalidFileNameChars(fileName);
        return string.IsNullOrWhiteSpace(sanitizedFileName) ? null : sanitizedFileName;
    }

    public static bool IsSupportedArchive(Download download)
    {
        var extension = Path.GetExtension(GetFileName(download));

        return string.Equals(extension, ".rar", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetTorrentDirectoryName(Torrent torrent)
    {
        var directoryName = FileHelper.RemoveInvalidFileNameChars(torrent.RdName ?? string.Empty);

        return string.IsNullOrWhiteSpace(directoryName)
            ? torrent.Hash
            : directoryName;
    }

    private static string GetRelativeDownloadPath(Torrent torrent, string fileName)
    {
        var torrentPath = GetTorrentDirectoryName(torrent);
        var subPath = FindSubPath(torrent, fileName);

        if (subPath != null)
        {
            torrentPath = Path.Combine(torrentPath, subPath);
        }

        return Path.Combine(torrentPath, fileName);
    }
}
