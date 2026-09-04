using AdbClient.Data.Enums;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.Internal;
using AdbClient.Service.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdbClient.Service.BackgroundServices;

public class ProviderUpdater(ILogger<ProviderUpdater> logger, IServiceProvider serviceProvider) : BackgroundService
{
    private static DateTime _nextUpdate = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!Startup.Ready)
        {
            await Task.Delay(1000, stoppingToken);
        }

        using var scope = serviceProvider.CreateScope();
        var torrentService = scope.ServiceProvider.GetRequiredService<Torrents>();

        logger.LogInformation("ProviderUpdater started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var torrents = await torrentService.Get();

                if (_nextUpdate < DateTime.UtcNow && ShouldReconcileProvider(Settings.Get.Provider, torrents))
                {
                    logger.LogDebug($"Updating torrent info from debrid provider");

                    var updateTime = Settings.Get.Provider.CheckInterval * 3;

                    if (updateTime < 30)
                    {
                        updateTime = 30;
                    }

                    if (AdbHub.HasConnections)
                    {
                        updateTime = Settings.Get.Provider.CheckInterval;

                        if (updateTime < 5)
                        {
                            updateTime = 5;
                        }
                    }

                    _nextUpdate = DateTime.UtcNow.AddSeconds(updateTime);

                    await torrentService.UpdateRdData();

                    logger.LogDebug("Finished updating torrent info from debrid provider, next update in {updateTime} seconds", updateTime);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error occurred in ProviderUpdater: {ex.Message}", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }

        logger.LogInformation("ProviderUpdater stopped.");
    }

    internal static bool ShouldReconcileProvider(DbSettingsProvider settings, IEnumerable<Torrent> torrents)
    {
        return settings.AutoImport ||
               settings.AutoDelete ||
               torrents.Any(torrent => torrent.RdStatus != TorrentStatus.Finished);
    }
}
