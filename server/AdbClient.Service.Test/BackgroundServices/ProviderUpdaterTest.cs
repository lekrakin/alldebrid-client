using AdbClient.Data.Enums;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.Internal;
using AdbClient.Service.BackgroundServices;

namespace AdbClient.Service.Test.BackgroundServices;

public class ProviderUpdaterTest
{
    [Theory]
    [InlineData(false, true, TorrentStatus.Finished, true)]
    [InlineData(true, false, TorrentStatus.Finished, true)]
    [InlineData(false, false, TorrentStatus.Downloading, true)]
    [InlineData(false, false, TorrentStatus.Finished, false)]
    public void ShouldReconcileProvider_HonorsSynchronizationSettings(
        bool autoImport,
        bool autoDelete,
        TorrentStatus status,
        bool expected)
    {
        var settings = new DbSettingsProvider
        {
            AutoImport = autoImport,
            AutoDelete = autoDelete
        };
        Torrent[] torrents = [new() { RdStatus = status }];

        var result = ProviderUpdater.ShouldReconcileProvider(settings, torrents);

        Assert.Equal(expected, result);
    }
}
