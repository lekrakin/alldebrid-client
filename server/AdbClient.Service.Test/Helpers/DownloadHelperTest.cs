using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.TorrentClient;
using AdbClient.Service.Helpers;

namespace AdbClient.Service.Test.Helpers;

public class DownloadHelperTest
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void GetCategoryPath_RejectsEmptyDownloadRoot(string downloadRoot)
    {
        Assert.Throws<InvalidDataException>(() =>
            DownloadHelper.GetCategoryPath(downloadRoot, "radarr", new MockFileSystem()));
    }

    [Fact]
    public void GetCategoryPath_RejectsTraversalOutsideDownloadRoot()
    {
        var downloadRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adbclient-download-path-tests"));

        Assert.Throws<InvalidDataException>(() =>
            DownloadHelper.GetCategoryPath(downloadRoot, "../outside", new MockFileSystem()));
    }

    [Fact]
    public void GetCategoryPath_RejectsExistingReparsePoint()
    {
        var downloadRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adbclient-download-path-tests"));
        var categoryPath = Path.Combine(downloadRoot, "radarr");
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(categoryPath);
        fileSystem.File.SetAttributes(
            categoryPath,
            fileSystem.File.GetAttributes(categoryPath) | FileAttributes.ReparsePoint);

        Assert.Throws<InvalidDataException>(() =>
            DownloadHelper.GetCategoryPath(downloadRoot, "radarr", fileSystem));
    }

    [Fact]
    public void GetDownloadPath_WithPath_WhenRdNameNull_ReturnsNull()
    {
        // Arrange
        var download = new Download
        {
            Link = "https://fake.url/file.txt",
            FileName = "file.txt"
        };

        var torrent = new Torrent
        {
            RdName = null
        };

        // Act
        var path = DownloadHelper.GetDownloadPath("/data/downloads", torrent, download);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void GetDownloadPath_WithoutPath_WhenRdNameNull_ReturnsNull()
    {
        // Arrange
        var download = new Download
        {
            Link = "https://fake.url/file.txt",
            FileName = "file.txt"
        };

        var torrent = new Torrent
        {
            RdName = null
        };

        // Act
        var path = DownloadHelper.GetDownloadPath(torrent, download);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void GetDownloadPath_WithPath_WhenDownloadLinkNull_ReturnsNull()
    {
        // Arrange
        var download = new Download
        {
            Link = null,
            FileName = "file.txt"
        };

        var torrent = new Torrent
        {
            RdName = "Torrent Name"
        };

        // Act
        var path = DownloadHelper.GetDownloadPath("/data/downloads", torrent, download);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void GetDownloadPath_WithoutPath_UsesStoredFileNameWithoutDownloadLink()
    {
        // Arrange
        var download = new Download
        {
            Link = null,
            FileName = "file.txt"
        };

        var torrent = new Torrent
        {
            RdName = "Torrent Name"
        };

        // Act
        var path = DownloadHelper.GetDownloadPath(torrent, download);

        // Assert
        Assert.Equal(Path.Combine("Torrent Name", "file.txt"), path);
    }

    [Fact]
    public void GetDownloadPath_WithPath_WhenDownloadFileNameNull_UsesLinkToGuessFileName()
    {
        // Arrange
        var download = new Download
        {
            Link = "https://fake.url/filename-from-link.txt",
            FileName = null
        };

        var torrent = new Torrent
        {
            RdName = "Torrent Name"
        };

        var fileSystem = new MockFileSystem();

        // Act
        var path = DownloadHelper.GetDownloadPath("/data/downloads", torrent, download, fileSystem);

        // Assert
        var expectedPath = Path.Combine("/data/downloads", torrent.RdName, "filename-from-link.txt");
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void GetDownloadPath_WithoutPath_WhenDownloadFileNameNull_UsesLinkToGuessFileName()
    {
        // Arrange
        var download = new Download
        {
            Link = "https://fake.url/filename-from-link.txt",
            FileName = null
        };

        var torrent = new Torrent
        {
            RdName = "Torrent Name"
        };

        // Act
        var path = DownloadHelper.GetDownloadPath(torrent, download);

        // Assert
        var expectedPath = Path.Combine(torrent.RdName, "filename-from-link.txt");
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void GetDownloadPath_WithPath_WhenValid_CreatesDirectory()
    {
        // Arrange
        var download = new Download
        {
            Link = "https://fake.url/file.txt",
            FileName = "file.txt"
        };

        var torrent = new Torrent
        {
            RdName = "Torrent Name"
        };

        var fileSystem = new MockFileSystem();

        // Act
        var path = DownloadHelper.GetDownloadPath("/data/downloads", torrent, download, fileSystem);

        // Assert
        var expectedDirectoryPath = Path.Combine("/data/downloads", torrent.RdName);
        Assert.True(fileSystem.Directory.Exists(expectedDirectoryPath));
        var expectedPath = Path.Combine(expectedDirectoryPath, download.FileName);
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void GetDownloadPath_WithPath_WhenFileInSubdirectories_ReturnsPathWithSubdirectories()
    {
        // Arrange
        var download = new Download
        {
            Link = "https://fake.url/file.txt",
            FileName = "file.txt"
        };

        var fileRelativePath = "inside/lots/of/subdirectories/file.txt";

        IList<TorrentClientFile> files =
        [
            new()
            {
                Path = fileRelativePath
            }
        ];

        var torrent = new Torrent
        {
            RdName = "Torrent Name",
            RdFiles = JsonSerializer.Serialize(files)
        };

        var fileSystem = new MockFileSystem();

        // Act
        var path = DownloadHelper.GetDownloadPath("/data/downloads", torrent, download, fileSystem);

        // Assert
        var expectedPath = Path.Combine("/data/downloads",
                                        torrent.RdName,
                                        fileRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void GetDownloadPath_WithoutPath_WhenFileInSubdirectories_ReturnsPathWithSubdirectories()
    {
        // Arrange
        var download = new Download
        {
            Link = "https://fake.url/file.txt",
            FileName = "file.txt"
        };

        var fileRelativePath = "inside/lots/of/subdirectories/file.txt";

        IList<TorrentClientFile> files =
        [
            new()
            {
                Path = fileRelativePath
            }
        ];

        var torrent = new Torrent
        {
            RdName = "Torrent Name",
            RdFiles = JsonSerializer.Serialize(files)
        };

        // Act
        var path = DownloadHelper.GetDownloadPath(torrent, download);

        // Assert
        var expectedPath = Path.Combine(torrent.RdName,
                                        fileRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void GetDownloadPath_WithPath_WhenNoFileNameCanBeResolved_ReturnsNull()
    {
        // Arrange
        var download = new Download
        {
            Link = "https://fake.url", // HttpUtility.UrlDecode(new Uri("https://fake.url").Segments.Last()) == "/"
            FileName = null
        };

        var torrent = new Torrent
        {
            RdName = "Torrent Name"
        };

        var fileSystem = new MockFileSystem();

        // Act
        var path = DownloadHelper.GetDownloadPath("/data/downloads", torrent, download, fileSystem);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void GetDownloadPath_SanitizesTorrentMetadataWithinDownloadRoot()
    {
        var downloadRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adbclient-download-path-tests"));
        var download = new Download
        {
            Link = "https://fake.url/episode.mkv",
            FileName = "episode?.mkv"
        };
        var torrent = new Torrent
        {
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdName = "../outside",
            RdFiles = JsonSerializer.Serialize(new[]
            {
                new TorrentClientFile { Path = "../../outside/episode?.mkv" }
            })
        };
        var fileSystem = new MockFileSystem();

        var path = DownloadHelper.GetDownloadPath(downloadRoot, torrent, download, fileSystem);

        Assert.NotNull(path);
        Assert.StartsWith(
            downloadRoot + Path.DirectorySeparatorChar,
            Path.GetFullPath(path),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Assert.DoesNotContain($"{Path.DirectorySeparatorChar}..{Path.DirectorySeparatorChar}", path, StringComparison.Ordinal);
        Assert.EndsWith("episode.mkv", path, StringComparison.Ordinal);
    }

    [Fact]
    public void GetDownloadPath_RelativeAndPhysicalOverloadsUseSameSanitizedName()
    {
        var downloadRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adbclient-download-path-tests"));
        var download = new Download
        {
            Link = "https://fake.url/episode.mkv",
            FileName = "episode:01?.mkv"
        };
        var torrent = new Torrent
        {
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdName = "Series"
        };
        var fileSystem = new MockFileSystem();

        var relativePath = DownloadHelper.GetDownloadPath(torrent, download);
        var physicalPath = DownloadHelper.GetDownloadPath(downloadRoot, torrent, download, fileSystem);

        Assert.Equal(Path.Combine("Series", "episode01.mkv"), relativePath);
        Assert.Equal(Path.Combine(downloadRoot, relativePath!), physicalPath);
    }

    [Fact]
    public void GetDownloadPath_RejectsExistingReparsePoint()
    {
        var downloadRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adbclient-download-path-tests"));
        var torrentDirectory = Path.Combine(downloadRoot, "Series");
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(torrentDirectory);
        fileSystem.File.SetAttributes(
            torrentDirectory,
            fileSystem.File.GetAttributes(torrentDirectory) | FileAttributes.ReparsePoint);
        var download = new Download
        {
            Link = "https://fake.url/episode.mkv",
            FileName = "episode.mkv"
        };
        var torrent = new Torrent
        {
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdName = "Series"
        };

        Assert.Throws<InvalidDataException>(() =>
            DownloadHelper.GetDownloadPath(downloadRoot, torrent, download, fileSystem));
    }

    [Theory]
    [InlineData("release.rar", true)]
    [InlineData("release.RAR", true)]
    [InlineData("release.zip", true)]
    [InlineData("release.ZIP", true)]
    [InlineData("movie.mkv", false)]
    [InlineData(null, false)]
    public void IsSupportedArchive_MatchesActualUnpackFormats(string? fileName, bool expected)
    {
        var download = new Download
        {
            Link = fileName == null ? null : $"https://fake.url/{fileName}",
            FileName = fileName
        };

        Assert.Equal(expected, DownloadHelper.IsSupportedArchive(download));
    }
}
