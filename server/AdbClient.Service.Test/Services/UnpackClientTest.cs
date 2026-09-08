using AdbClient.Data.Models.Data;
using AdbClient.Service.Services;

namespace AdbClient.Service.Test.Services;

public class UnpackClientTest
{
    [Fact]
    public async Task CancelBeforeStart_CompletesAndPreventsLaterWork()
    {
        var torrent = new Torrent
        {
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdName = "cancel-before-start"
        };
        var download = new Download
        {
            Torrent = torrent,
            Link = "https://example.invalid/archive.zip",
            FileName = "archive.zip"
        };
        var client = new UnpackClient(download, "unused");

        client.Cancel();
        client.Cancel();
        await client.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(1));
        client.Start();

        Assert.True(client.Finished);
        Assert.Equal("The unpack was cancelled", client.Error);
    }

    [Fact]
    public void ResolveExtractionPath_SanitizesTorrentNameWithinDestination()
    {
        var destination = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adbclient-unpack-tests"));
        var torrent = new Torrent
        {
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdName = "../outside"
        };

        var result = UnpackClient.ResolveExtractionPath(destination, torrent, ["movie.mkv"]);

        Assert.StartsWith(
            destination + Path.DirectorySeparatorChar,
            result,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Assert.Equal(Path.Combine(destination, "..outside"), result);
    }

    [Fact]
    public void ResolveExtractionPath_MixedTorrentAndSiblingRootsRemainInsideJobDirectory()
    {
        var destination = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adbclient-unpack-tests"));
        var torrent = new Torrent
        {
            Hash = "0123456789abcdef0123456789abcdef01234567",
            RdName = "Movie"
        };

        var result = UnpackClient.ResolveExtractionPath(
            destination,
            torrent,
            ["Movie/movie.mkv", "OtherJob/overwrite.mkv"]);

        Assert.Equal(Path.Combine(destination, "Movie"), result);
    }

}
