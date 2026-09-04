using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AdbClient.Service.Services;

namespace AdbClient.Service.BackgroundServices;

public class TaskRunner(ILogger<TaskRunner> logger, IServiceProvider serviceProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!Startup.Ready)
        {
            await Task.Delay(1000, stoppingToken);
        }

        using var scope = serviceProvider.CreateScope();
        var torrentRunner = scope.ServiceProvider.GetRequiredService<TorrentRunner>();

        logger.LogInformation("TaskRunner started.");

        await torrentRunner.Initialize();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await torrentRunner.Tick();
            }
            catch (DbUpdateConcurrencyException ex)
            {
                foreach (var entry in ex.Entries)
                {
                    logger.LogWarning(
                        "Database concurrency conflict while processing {EntityType}; the next runner tick will reload current state.",
                        entry.Metadata.ClrType.Name);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Unexpected error occurred in TaskRunner: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }

        logger.LogInformation("TaskRunner stopped.");
    }
}
