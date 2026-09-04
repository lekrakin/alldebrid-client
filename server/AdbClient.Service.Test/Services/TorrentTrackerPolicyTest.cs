using System.IO.Abstractions.TestingHelpers;
using System.Text;
using AdbClient.Data.Data;
using AdbClient.Data.Models.Data;
using AdbClient.Service.Services;
using AdbClient.Service.Wrappers;
using Microsoft.Extensions.Logging;
using MonoTorrent.BEncoding;
using Moq;

namespace AdbClient.Service.Test.Services;

[Collection(SettingsIsolationCollection.Name)]
public class TorrentTrackerPolicyTest
{
    private const string InfoHash = "0123456789abcdef0123456789abcdef01234567";
    private const string OriginalMagnet = $"magnet:?xt=urn:btih:{InfoHash}&dn=Episode";

    [Fact]
    public async Task AddMagnetToDebridQueue_ValidatesTrackersAddedByEnrichment()
    {
        var originalBlockedTrackers = Settings.Get.Provider.BannedTrackers;
        var torrentData = new Mock<ITorrentData>();
        var enricher = new Mock<IEnricher>();
        var enrichedMagnet = $"{OriginalMagnet}&tr={Uri.EscapeDataString("https://PRIVATE.example/announce")}";
        enricher.Setup(value => value.EnrichMagnetLink(OriginalMagnet)).ReturnsAsync(enrichedMagnet);
        var service = CreateService(torrentData, enricher);

        try
        {
            Settings.Get.Provider.BannedTrackers = " private.example ";

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.AddMagnetToDebridQueue(OriginalMagnet, new Torrent()));

            Assert.Contains("blocked trackers", exception.Message, StringComparison.OrdinalIgnoreCase);
            torrentData.VerifyNoOtherCalls();
        }
        finally
        {
            Settings.Get.Provider.BannedTrackers = originalBlockedTrackers;
        }
    }

    [Fact]
    public async Task AddFileToDebridQueue_ValidatesSourceAddedByEnrichmentCaseInsensitively()
    {
        var originalBlockedTrackers = Settings.Get.Provider.BannedTrackers;
        var torrentData = new Mock<ITorrentData>();
        var enricher = new Mock<IEnricher>();
        var originalBytes = Encoding.ASCII.GetBytes(
            "d4:infod6:lengthi1e4:name11:episode.mkv12:piece lengthi16384e6:pieces20:00000000000000000000ee");
        var enrichedDictionary = BEncodedValue.Decode<BEncodedDictionary>(originalBytes);
        var info = (BEncodedDictionary)enrichedDictionary["info"];
        info["source"] = new BEncodedString("PRIVATE-SITE");
        var enrichedBytes = enrichedDictionary.Encode();
        enricher.Setup(value => value.EnrichTorrentBytes(originalBytes)).ReturnsAsync(enrichedBytes);
        var service = CreateService(torrentData, enricher);

        try
        {
            Settings.Get.Provider.BannedTrackers = " private-site ";

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.AddFileToDebridQueue(originalBytes, new Torrent()));

            Assert.Contains("source", exception.Message, StringComparison.OrdinalIgnoreCase);
            torrentData.VerifyNoOtherCalls();
        }
        finally
        {
            Settings.Get.Provider.BannedTrackers = originalBlockedTrackers;
        }
    }

    private static Torrents CreateService(Mock<ITorrentData> torrentData, Mock<IEnricher> enricher)
    {
        return new(
            Mock.Of<ILogger<Torrents>>(),
            torrentData.Object,
            Mock.Of<IDownloads>(),
            Mock.Of<IProcessFactory>(),
            new MockFileSystem(),
            enricher.Object,
            null!);
    }
}
