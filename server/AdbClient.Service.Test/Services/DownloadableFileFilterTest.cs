using Microsoft.Extensions.Logging;
using Moq;
using System.Text.RegularExpressions;
using AdbClient.Data.Models.Data;
using AdbClient.Service.Services;

namespace AdbClient.Service.Test.Services;

public class DownloadableFileFilterTest
{
    [Fact]
    public void IsDownloadable_WhenNoFilterSpecified_ReturnsTrue()
    {
        // Arrange
        var mocks = new Mocks();

        var torrent = new Torrent
        {
            RdId = "1"
        };

        var fileFilter = new DownloadableFileFilter(mocks.LoggerMock.Object);

        // Act
        var result = fileFilter.IsDownloadable(torrent, "file.txt", 10000);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDownloadable_WhenMinimumSizeIsIntMaxValue_DoesNotOverflow()
    {
        var torrent = new Torrent
        {
            RdId = "1",
            DownloadMinSize = int.MaxValue
        };
        var fileFilter = new DownloadableFileFilter(Mock.Of<ILogger<DownloadableFileFilter>>());

        var result = fileFilter.IsDownloadable(torrent, "file.txt", 1);

        Assert.False(result);
    }

    [Fact]
    public void IsDownloadable_WhenIncludeRegexTimesOut_FailsClosedAndLogsWarning()
    {
        var logger = new Mock<ILogger<DownloadableFileFilter>>();
        var torrent = new Torrent
        {
            RdId = "1",
            IncludeRegex = "^(a+)+$"
        };
        var fileFilter = new DownloadableFileFilter(logger.Object);
        var pathologicalPath = new string('a', 100_000) + "!";

        var result = fileFilter.IsDownloadable(torrent, pathologicalPath, long.MaxValue);

        Assert.False(result);
        logger.Verify(
            entry => entry.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((value, _) => value.ToString()!.Contains("safety limit")),
                It.IsAny<RegexMatchTimeoutException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Theory]
    // downloadMinSize is in MB, fileSize is in B
    [InlineData(100, 20 * 1024 * 1024)]
    [InlineData(2, 2 * 1024 * 1024)]
    [InlineData(2, 2 * (1000 * 1000 + 1))] // mostly to show we use 1024 not 1000 for conversion
    public void IsDownloadable_WhenDownloadMinSizeSpecified_AndDownloadBelowSize_ReturnsFalse(int downloadMinSize, long fileSize)
    {
        // Arrange
        var mocks = new Mocks();

        var torrent = new Torrent
        {
            RdId = "1",
            DownloadMinSize = downloadMinSize
        };

        var fileFilter = new DownloadableFileFilter(mocks.LoggerMock.Object);

        // Act
        var result = fileFilter.IsDownloadable(torrent, "file.txt", fileSize);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(100, 110 * 1024 * 1024)]
    [InlineData(2, 2 * 1024 * 1024 + 1)]
    public void IsDownloadable_WhenDownloadMinSizeSpecified_AndDownloadAboveSize_ReturnsTrue(int downloadMinSize, long fileSize)
    {
        // Arrange
        var mocks = new Mocks();

        var torrent = new Torrent
        {
            RdId = "1",
            DownloadMinSize = downloadMinSize
        };

        var fileFilter = new DownloadableFileFilter(mocks.LoggerMock.Object);

        // Act
        var result = fileFilter.IsDownloadable(torrent, "file.txt", fileSize);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData("file", "no-match")]
    [InlineData("file", "even/in/a/subdirectory.txt")]
    [InlineData("ch[aA]racter c[lL]asses", "nope.txt")]
    [InlineData("digits\\d+", "123 not matching.txt")]
    public void IsDownloadable_WhenIncludeRegexSpecified_AndPathDoesNotMatchRegex_ReturnsFalse(string includeRegex, string filePath)
    {
        // Arrange
        var mocks = new Mocks();

        var torrent = new Torrent
        {
            RdId = "1",
            IncludeRegex = includeRegex
        };

        var fileFilter = new DownloadableFileFilter(mocks.LoggerMock.Object);

        // Act
        var result = fileFilter.IsDownloadable(torrent, filePath, long.MaxValue);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("file", "file.txt")]
    [InlineData("file", "file/in/a/subdirectory.txt")]
    [InlineData("ch[aA]racter c[lL]asses", "character cLasses")]
    [InlineData("digits\\d+", "digits123456.txt")]
    public void IsDownloadable_WhenIncludeRegexSpecified_AndPathMatchesRegex_ReturnsTrue(string includeRegex, string filePath)
    {
        // Arrange
        var mocks = new Mocks();

        var torrent = new Torrent
        {
            RdId = "1",
            IncludeRegex = includeRegex
        };

        var fileFilter = new DownloadableFileFilter(mocks.LoggerMock.Object);

        // Act
        var result = fileFilter.IsDownloadable(torrent, filePath, long.MaxValue);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData("file", "no-match")]
    [InlineData("file", "even/in/a/subdirectory.txt")]
    [InlineData("ch[aA]racter c[lL]asses", "nope.txt")]
    [InlineData("digits\\d+", "123 not matching.txt")]
    public void IsDownloadable_WhenExcludeRegexSpecified_AndPathDoesNotMatchRegex_ReturnsTrue(string excludeRegex, string filePath)
    {
        // Arrange
        var mocks = new Mocks();

        var torrent = new Torrent
        {
            RdId = "1",
            ExcludeRegex = excludeRegex
        };

        var fileFilter = new DownloadableFileFilter(mocks.LoggerMock.Object);

        // Act
        var result = fileFilter.IsDownloadable(torrent, filePath, long.MaxValue);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData("file", "file.txt")]
    [InlineData("file", "file/in/a/subdirectory.txt")]
    [InlineData("ch[aA]racter c[lL]asses", "character cLasses")]
    [InlineData("digits\\d+", "digits123456.txt")]
    public void IsDownloadable_WhenExcludeRegexSpecified_AndPathMatchesRegex_ReturnsFalse(string excludeRegex, string filePath)
    {
        // Arrange
        var mocks = new Mocks();

        var torrent = new Torrent
        {
            RdId = "1",
            ExcludeRegex = excludeRegex
        };

        var fileFilter = new DownloadableFileFilter(mocks.LoggerMock.Object);

        // Act
        var result = fileFilter.IsDownloadable(torrent, filePath, long.MaxValue);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("file", "file", "file.txt")]
    [InlineData("file", "in/a", "file/in/a/subdirectory.txt")]
    [InlineData("ch[aA]racter c[lL]asses", "character", "character cLasses")]
    [InlineData("digits\\d+", "123456", "digits123456.txt")]
    public void IsDownloadable_WhenBothIncludeAndExcludeRegexSpecified_AndPathMatchesIncludeAndExcludeRegex_ReturnsTrue(string includeRegex, string excludeRegex, string filePath)
    {
        // Arrange
        var mocks = new Mocks();

        var torrent = new Torrent
        {
            RdId = "1",
            IncludeRegex = includeRegex,
            ExcludeRegex = excludeRegex
        };

        var fileFilter = new DownloadableFileFilter(mocks.LoggerMock.Object);

        // Act
        var result = fileFilter.IsDownloadable(torrent, filePath, long.MaxValue);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(10, "file", 10 * 1024 * 1024 + 1, "no-match.txt")]
    public void IsDownloadable_WhenBothDownloadMinSizeAndIncludeRegexSpecified_AndDownloadAboveSizeAndDoesNotMatchRegex_ReturnsFalse(
        int minSize,
        string includeRegex,
        long fileSize,
        string filePath)
    {
        // Arrange
        var mocks = new Mocks();

        var torrent = new Torrent
        {
            RdId = "1",
            IncludeRegex = includeRegex,
            DownloadMinSize = minSize
        };

        var fileFilter = new DownloadableFileFilter(mocks.LoggerMock.Object);

        // Act
        var result = fileFilter.IsDownloadable(torrent, filePath, fileSize);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(10, "file", 10 * 1024 * 1024 - 1, "file.txt")]
    public void IsDownloadable_WhenBothDownloadMinSizeAndIncludeRegexSpecified_AndDownloadBelowSizeAndMatchesRegex_ReturnsFalse(
        int minSize,
        string includeRegex,
        long fileSize,
        string filePath)
    {
        // Arrange
        var mocks = new Mocks();

        var torrent = new Torrent
        {
            RdId = "1",
            IncludeRegex = includeRegex,
            DownloadMinSize = minSize
        };

        var fileFilter = new DownloadableFileFilter(mocks.LoggerMock.Object);

        // Act
        var result = fileFilter.IsDownloadable(torrent, filePath, fileSize);

        // Assert
        Assert.False(result);
    }
    private class Mocks
    {
        public readonly Mock<ILogger<DownloadableFileFilter>> LoggerMock = new();
    }
}
