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

    [Theory]
    [InlineData(1, true, 5)]
    [InlineData(10, true, 10)]
    [InlineData(1, false, 30)]
    [InlineData(11, false, 33)]
    public void GetUpdateInterval_AppliesConnectionCadenceAndMinimums(
        int checkInterval,
        bool hasConnections,
        int expectedSeconds)
    {
        var settings = new DbSettingsProvider { CheckInterval = checkInterval };

        var result = ProviderUpdater.GetUpdateInterval(settings, hasConnections);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result);
    }

    [Fact]
    public void GetUpdateInterval_MaximumSettingDoesNotOverflow()
    {
        var settings = new DbSettingsProvider { CheckInterval = int.MaxValue };

        var result = ProviderUpdater.GetUpdateInterval(settings, hasConnections: false);

        Assert.Equal(TimeSpan.FromSeconds((long)int.MaxValue * 3), result);
    }
}
