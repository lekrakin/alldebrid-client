using AdbClient.Data.Enums;
using AdbClient.Data.Models.Data;
using AdbClient.Service.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace AdbClient.Service.BackgroundServices;

public class WatchFolderChecker(ILogger<WatchFolderChecker> logger, IServiceProvider serviceProvider) : BackgroundService
{
    private DateTime _prevCheck = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!Startup.Ready)
        {
            await Task.Delay(1000, stoppingToken);
        }

        using var scope = serviceProvider.CreateScope();
        var torrentService = scope.ServiceProvider.GetRequiredService<Torrents>();

        logger.LogInformation("WatchFolderChecker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, stoppingToken);

                if (string.IsNullOrWhiteSpace(Settings.Get.WatchFolder.InboxPath))
                {
                    continue;
                }

                var processedStorePath = Path.Combine(Settings.Get.WatchFolder.InboxPath, "processed");
                var errorStorePath = Path.Combine(Settings.Get.WatchFolder.InboxPath, "error");

                if (!string.IsNullOrWhiteSpace(Settings.Get.WatchFolder.ProcessedPath))
                {
                    processedStorePath = Settings.Get.WatchFolder.ProcessedPath;
                }

                if (!string.IsNullOrWhiteSpace(Settings.Get.WatchFolder.ErrorPath))
                {
                    errorStorePath = Settings.Get.WatchFolder.ErrorPath;
                }

                var nextCheck = _prevCheck.AddSeconds(Settings.Get.WatchFolder.Interval);

                if (DateTime.UtcNow < nextCheck)
                {
                    continue;
                }

                _prevCheck = DateTime.UtcNow;

                var torrentFiles = Directory.GetFiles(Settings.Get.WatchFolder.InboxPath, "*.*", SearchOption.TopDirectoryOnly);

                foreach (var torrentFile in torrentFiles)
                {
                    var fileInfo = new FileInfo(torrentFile);

                    if (fileInfo.Extension != ".magnet" && fileInfo.Extension != ".torrent")
                    {
                        continue;
                    }

                    if (IsFileLocked(fileInfo))
                    {
                        continue;
                    }

                    try
                    {
                        logger.Log(LogLevel.Debug, "Processing {torrentFile}", torrentFile);

                        var torrent = new Torrent
                        {
                            DownloadClient = Data.Enums.DownloadClient.Internal,
                            Category = Settings.Get.Downloads.Defaults.Category,
                            HostDownloadAction = Settings.Get.Downloads.Defaults.HostDownloadAction,
                            FinishedActionDelay = Settings.Get.Downloads.Defaults.FinishedActionDelay,
                            FinishedAction = Settings.Get.Downloads.Defaults.FinishedAction,
                            DownloadMinSize = Settings.Get.Downloads.Defaults.MinFileSize,
                            IncludeRegex = Settings.Get.Downloads.Defaults.IncludeRegex,
                            ExcludeRegex = Settings.Get.Downloads.Defaults.ExcludeRegex,
                            TorrentRetryAttempts = Settings.Get.Downloads.Defaults.TorrentRetryAttempts,
                            DownloadRetryAttempts = Settings.Get.Downloads.Defaults.DownloadRetryAttempts,
                            DeleteOnError = Settings.Get.Downloads.Defaults.DeleteOnError,
                            Lifetime = Settings.Get.Downloads.Defaults.TorrentLifetime,
                            Priority = Settings.Get.Downloads.Defaults.Priority > 0 ? Settings.Get.Downloads.Defaults.Priority : null
                        };

                        if (fileInfo.Extension == ".torrent")
                        {
                            var torrentFileContents = await File.ReadAllBytesAsync(torrentFile, stoppingToken);
                            await torrentService.AddFileToDebridQueue(torrentFileContents, torrent);
                        }
                        else if (fileInfo.Extension == ".magnet")
                        {
                            var magnetLink = await File.ReadAllTextAsync(torrentFile, stoppingToken);
                            await torrentService.AddMagnetToDebridQueue(magnetLink, torrent);
                        }

                        if (!Directory.Exists(processedStorePath))
                        {
                            Directory.CreateDirectory(processedStorePath);
                        }

                        var processedPath = Path.Combine(processedStorePath, fileInfo.Name);

                        if (File.Exists(processedPath))
                        {
                            File.Delete(processedPath);

                            logger.Log(LogLevel.Warning,
                                       "File {torrentFileName} replaced in {processedStorePath} - it already existed and new torrent with same filename was added",
                                       fileInfo.Name,
                                       processedStorePath);
                        }

                        File.Move(torrentFile, processedPath);

                        logger.Log(LogLevel.Debug, "Moved {torrentFile} to {processedPath}", torrentFile, processedPath);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        if (!Directory.Exists(errorStorePath))
                        {
                            Directory.CreateDirectory(errorStorePath);
                        }

                        var processedPath = Path.Combine(errorStorePath, fileInfo.Name);

                        if (File.Exists(processedPath))
                        {
                            File.Delete(processedPath);

                            logger.Log(LogLevel.Warning,
                                       "File {torrentFileName} replaced in {errorStorePath} - it already existed and new torrent with same filename was added",
                                       fileInfo.Name,
                                       errorStorePath);
                        }
                        File.Move(torrentFile, processedPath);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error occurred in WatchFolderChecker.");
            }
        }
    }

    private static bool IsFileLocked(FileInfo file)
    {
        try
        {
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None);
            stream.Close();
        }
        catch (IOException e) when ((e.HResult & 0x0000FFFF) == 32)
        {
            return true;
        }
        return false;
    }
}
