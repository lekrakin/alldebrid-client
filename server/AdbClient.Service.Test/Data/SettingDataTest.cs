using AdbClient.Data.Data;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.Internal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace AdbClient.Service.Test.Data;

public class SettingDataTest
{
    [Fact]
    public void GetAll_ExposesInputConstraintsAndSecretMetadata()
    {
        var settings = SettingData.GetAll().ToDictionary(setting => setting.Key);

        Assert.Equal(1, settings["Downloads:ConcurrentFiles"].Minimum);
        Assert.Equal(16, settings["Downloads:ConnectionsPerFile"].Maximum);
        Assert.Equal(1, settings["Integrations:CompletionCommand:TimeoutSeconds"].Minimum);
        Assert.Equal(3600, settings["Integrations:CompletionCommand:TimeoutSeconds"].Maximum);
        Assert.True(settings["Provider:ApiKey"].IsSecret);
        Assert.False(settings["Storage:DownloadPath"].IsSecret);
    }

    [Fact]
    public async Task Seed_NewDatabase_UsesUsablePlatformPathWithoutReportedOverride()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                      .UseSqlite(connection)
                      .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        var settings = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);
        var expectedDownloadPath = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "AllDebridClient",
                "downloads")
            : "/data/downloads";

        Assert.Equal(expectedDownloadPath, settings["Storage:DownloadPath"].Value);
        Assert.Null(settings["Integrations:ReportedDownloadPath"].Value);

        await settingData.ResetCache();
        Assert.Equal(AdbClient.Data.Enums.LogLevel.Warning, SettingData.Get.General.LogLevel);
        Assert.Equal(AdbClient.Data.Enums.TorrentFinishedAction.None, SettingData.Get.Downloads.Defaults.FinishedAction);

    }

    [Fact]
    public async Task Seed_MigratesLegacySettingKeysWithoutLosingValues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        (string LegacyKey, string CurrentKey, string Value)[] migrations =
        [
            ("General:DownloadLimit", "Downloads:ConcurrentFiles", "4"),
            ("General:UnpackLimit", "Downloads:ConcurrentExtractions", "2"),
            ("General:Categories", "Integrations:Categories", "radarr,sonarr"),
            ("General:RunOnTorrentCompleteFileName", "Integrations:CompletionCommand:ExecutablePath", @"C:\Tools\finish.exe"),
            ("General:RunOnTorrentCompleteArguments", "Integrations:CompletionCommand:Arguments", "%N --path %F"),
            ("General:TrackerEnrichmentList", "Provider:TrackerEnrichmentList", "https://example.com/trackers.txt"),
            ("General:TrackerEnrichmentCacheExpiration", "Provider:TrackerEnrichmentCacheExpiration", "120"),
            ("General:BannedTrackers", "Provider:BannedTrackers", "private,internal"),
            ("DownloadClient:MaxSpeed", "Downloads:SpeedLimit", "25"),
            ("DownloadClient:ParallelCount", "Downloads:ConnectionsPerFile", "4"),
            ("DownloadClient:ParallelChunkCount", "Downloads:ChunksPerFile", "16"),
            ("DownloadClient:AutoImport", "Provider:AutoImport", "True"),
            ("DownloadClient:AutoDelete", "Provider:AutoDelete", "True"),
            ("DownloadClient:MaxParallelDownloads", "Provider:ConcurrentTorrents", "3"),
            ("DownloadClient:Default:HostDownloadAction", "Downloads:Defaults:HostDownloadAction", "0"),
            ("DownloadClient:Default:Category", "Downloads:Defaults:Category", "radarr"),
            ("DownloadClient:Default:FinishedAction", "Downloads:Defaults:FinishedAction", "0"),
            ("DownloadClient:Default:FinishedActionDelay", "Downloads:Defaults:FinishedActionDelay", "15"),
            ("DownloadClient:Default:MinFileSize", "Downloads:Defaults:MinFileSize", "10"),
            ("DownloadClient:Default:IncludeRegex", "Downloads:Defaults:IncludeRegex", @"\.mkv$"),
            ("DownloadClient:Default:ExcludeRegex", "Downloads:Defaults:ExcludeRegex", @"\.txt$"),
            ("DownloadClient:Default:TorrentRetryAttempts", "Downloads:Defaults:TorrentRetryAttempts", "5"),
            ("DownloadClient:Default:DownloadRetryAttempts", "Downloads:Defaults:DownloadRetryAttempts", "6"),
            ("DownloadClient:Default:DeleteOnError", "Downloads:Defaults:DeleteOnError", "30"),
            ("DownloadClient:Default:TorrentLifetime", "Downloads:Defaults:TorrentLifetime", "1440"),
            ("DownloadClient:Default:Priority", "Downloads:Defaults:Priority", "7"),
            ("Paths:DownloadPath", "Storage:DownloadPath", "/srv/downloads"),
            ("Paths:MappedPath", "Integrations:ReportedDownloadPath", "/media/downloads"),
            ("Paths:CopyAddedTorrents", "Integrations:AddedTorrentCopyPath", "/srv/torrent-copies"),
            ("Paths:WatchPath", "WatchFolder:InboxPath", "/srv/watch"),
            ("Paths:WatchErrorPath", "WatchFolder:ErrorPath", "/srv/watch-errors"),
            ("Paths:WatchProcessedPath", "WatchFolder:ProcessedPath", "/srv/watch-processed"),
            ("Watch:Interval", "WatchFolder:Interval", "90")
        ];

        dataContext.Settings.AddRange(migrations.Select(migration => new Setting
        {
            SettingId = migration.LegacyKey,
            Value = migration.Value
        }));
        await dataContext.SaveChangesAsync();
        dataContext.ChangeTracker.Clear();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        var settings = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);

        foreach (var migration in migrations)
        {
            Assert.DoesNotContain(migration.LegacyKey, settings.Keys);
            Assert.Equal(migration.Value, settings[migration.CurrentKey].Value);
        }
    }

    [Fact]
    public async Task Seed_MigratesDirect2022SettingsWithoutReplacingUserValues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        (string LegacyKey, string CurrentKey, string Value)[] migrations =
        [
            ("DownloadClient:DownloadPath", "Storage:DownloadPath", "/srv/direct-upgrade-downloads"),
            ("DownloadClient:MappedPath", "Integrations:ReportedDownloadPath", "/media/direct-upgrade-downloads"),
            ("Provider:Default:Category", "Downloads:Defaults:Category", "provider-import"),
            ("Provider:Default:MinFileSize", "Downloads:Defaults:MinFileSize", "20"),
            ("Provider:Default:TorrentRetryAttempts", "Downloads:Defaults:TorrentRetryAttempts", "8"),
            ("Provider:Default:DownloadRetryAttempts", "Downloads:Defaults:DownloadRetryAttempts", "9"),
            ("Provider:Default:DeleteOnError", "Downloads:Defaults:DeleteOnError", "45"),
            ("Provider:Default:TorrentLifetime", "Downloads:Defaults:TorrentLifetime", "2880")
        ];

        dataContext.Settings.AddRange(migrations.Select(migration => new Setting
        {
            SettingId = migration.LegacyKey,
            Value = migration.Value
        }));
        await dataContext.SaveChangesAsync();
        dataContext.ChangeTracker.Clear();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        var settings = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);

        foreach (var migration in migrations)
        {
            Assert.DoesNotContain(migration.LegacyKey, settings.Keys);
            Assert.Equal(migration.Value, settings[migration.CurrentKey].Value);
        }
    }

    [Fact]
    public async Task Seed_RemovesObsoleteAvailabilitySettings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        string[] obsoleteKeys =
        [
            "Downloads:Defaults:OnlyDownloadAvailableFiles",
            "DownloadClient:Default:OnlyDownloadAvailableFiles",
            "Provider:Default:OnlyDownloadAvailableFiles"
        ];
        dataContext.Settings.AddRange(obsoleteKeys.Select(key => new Setting
        {
            SettingId = key,
            Value = "True"
        }));
        await dataContext.SaveChangesAsync();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        var persistedKeys = await dataContext.Settings.AsNoTracking()
                                             .Select(setting => setting.SettingId)
                                             .ToListAsync();
        Assert.DoesNotContain(persistedKeys, key => obsoleteKeys.Contains(key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Seed_PrefersCurrentSettingWhenLegacyAndCurrentKeysBothExist()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        dataContext.Settings.AddRange(
            new Setting { SettingId = "General:DownloadLimit", Value = "4" },
            new Setting { SettingId = "Downloads:ConcurrentFiles", Value = "7" });
        await dataContext.SaveChangesAsync();
        dataContext.ChangeTracker.Clear();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        var settings = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);

        Assert.DoesNotContain("General:DownloadLimit", settings.Keys);
        Assert.Equal("7", settings["Downloads:ConcurrentFiles"].Value);
    }

    [Fact]
    public async Task Seed_RemovesRedundantReportedPathWithoutChangingDownloadPath()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                      .UseSqlite(connection)
                      .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        const string downloadPath = "/srv/downloads";
        dataContext.Settings.AddRange(
            new Setting { SettingId = "Paths:DownloadPath", Value = downloadPath },
            new Setting { SettingId = "Paths:MappedPath", Value = $"{downloadPath}/" });
        await dataContext.SaveChangesAsync();
        dataContext.ChangeTracker.Clear();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        var settings = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);

        Assert.Equal(downloadPath, settings["Storage:DownloadPath"].Value);
        Assert.Null(settings["Integrations:ReportedDownloadPath"].Value);
        Assert.DoesNotContain("Paths:DownloadPath", settings.Keys);
        Assert.DoesNotContain("Paths:MappedPath", settings.Keys);
    }

    [Fact]
    public async Task Seed_OnLinux_ReplacesUnusableLegacyWindowsDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                      .UseSqlite(connection)
                      .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        dataContext.Settings.AddRange(
            new Setting { SettingId = "Paths:DownloadPath", Value = @"C:\Downloads" },
            new Setting { SettingId = "Paths:MappedPath", Value = @"C:\Downloads" });
        await dataContext.SaveChangesAsync();
        dataContext.ChangeTracker.Clear();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        var settings = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);

        Assert.Equal("/data/downloads", settings["Storage:DownloadPath"].Value);
        Assert.Null(settings["Integrations:ReportedDownloadPath"].Value);
    }

    [Fact]
    public async Task Seed_ReplacesLegacyChunkSizeSettingWithParallelChunkCount()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                      .UseSqlite(connection)
                      .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        dataContext.Settings.Add(new Setting
        {
            SettingId = "DownloadClient:ChunkCount",
            Value = "50"
        });
        await dataContext.SaveChangesAsync();
        dataContext.ChangeTracker.Clear();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        var settings = await dataContext.Settings.AsNoTracking().ToListAsync();

        Assert.DoesNotContain(settings, setting => setting.SettingId == "DownloadClient:ChunkCount");
        Assert.Contains(settings, setting =>
            setting.SettingId == "Downloads:ChunksPerFile" && setting.Value == "0");
    }

    [Fact]
    public async Task Update_NormalizesListsPathsAndSensitiveValues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();
        await settingData.ResetCache();

        var downloadPath = SettingData.Get.Storage.DownloadPath;
        await settingData.Update([
            new SettingProperty { Key = "Integrations:Categories", Value = " radarr,SONARR,radarr " },
            new SettingProperty { Key = "Provider:BannedTrackers", Value = " private,PRIVATE, internal " },
            new SettingProperty { Key = "Integrations:ReportedDownloadPath", Value = $"{downloadPath}/" },
            new SettingProperty { Key = "Provider:ApiKey", Value = "  secret-token  " }
        ]);

        Assert.Equal("radarr,SONARR", SettingData.Get.Integrations.Categories);
        Assert.Equal("private,internal", SettingData.Get.Provider.BannedTrackers);
        Assert.Null(SettingData.Get.Integrations.ReportedDownloadPath);
        Assert.Equal("secret-token", SettingData.Get.Provider.ApiKey);

        var persisted = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);
        Assert.Equal("radarr,SONARR", persisted["Integrations:Categories"].Value);
        Assert.Equal("private,internal", persisted["Provider:BannedTrackers"].Value);
        Assert.Null(persisted["Integrations:ReportedDownloadPath"].Value);
        Assert.Equal("secret-token", persisted["Provider:ApiKey"].Value);
    }

    [Theory]
    [InlineData("Downloads:ConcurrentFiles", -1)]
    [InlineData("Downloads:ConnectionsPerFile", 17)]
    [InlineData("Provider:CheckInterval", 4)]
    [InlineData("Downloads:Defaults:TorrentRetryAttempts", 1001)]
    [InlineData("Integrations:CompletionCommand:TimeoutSeconds", 0)]
    public async Task Update_RejectsOutOfRangeValuesWithoutPersisting(string key, int value)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();
        await settingData.ResetCache();
        var originalValue = (await dataContext.Settings.AsNoTracking().SingleAsync(setting => setting.SettingId == key)).Value;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            settingData.Update([new SettingProperty { Key = key, Value = value }]));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        dataContext.ChangeTracker.Clear();
        var persistedValue = (await dataContext.Settings.AsNoTracking().SingleAsync(setting => setting.SettingId == key)).Value;
        Assert.Equal(originalValue, persistedValue);
    }

    [Theory]
    [InlineData("Downloads:Defaults:IncludeRegex", "[")]
    [InlineData("Provider:TrackerEnrichmentList", "file:///trackers.txt")]
    [InlineData("Downloads:Defaults:Category", "../outside")]
    [InlineData("Integrations:Categories", "radarr,../outside")]
    [InlineData("Storage:DownloadPath", "invalid\0path")]
    [InlineData("WatchFolder:InboxPath", "invalid\0path")]
    public async Task Update_RejectsValuesThatWouldFailDownstream(string key, string value)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();
        await settingData.ResetCache();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            settingData.Update([new SettingProperty { Key = key, Value = value }]));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_RejectsUnknownAndDuplicateKeys()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            settingData.Update([new SettingProperty { Key = "Unknown:Setting", Value = 1 }]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            settingData.Update([
                new SettingProperty { Key = "Downloads:ConcurrentFiles", Value = 2 },
                new SettingProperty { Key = "Downloads:ConcurrentFiles", Value = 3 }
            ]));
    }

    [Fact]
    public async Task Seed_ReplacesInvalidStoredValuesWithDefaults()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        dataContext.Settings.AddRange(
            new Setting { SettingId = "Downloads:ConcurrentFiles", Value = "0" },
            new Setting { SettingId = "Downloads:Defaults:IncludeRegex", Value = "[" },
            new Setting { SettingId = "Provider:TrackerEnrichmentList", Value = "not-a-url" });
        await dataContext.SaveChangesAsync();
        dataContext.ChangeTracker.Clear();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();
        await settingData.ResetCache();

        Assert.Equal(2, SettingData.Get.Downloads.ConcurrentFiles);
        Assert.Null(SettingData.Get.Downloads.Defaults.IncludeRegex);
        Assert.Null(SettingData.Get.Provider.TrackerEnrichmentList);

        var persisted = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);
        Assert.Equal("2", persisted["Downloads:ConcurrentFiles"].Value);
        Assert.Null(persisted["Downloads:Defaults:IncludeRegex"].Value);
        Assert.Null(persisted["Provider:TrackerEnrichmentList"].Value);
    }

    [Fact]
    public async Task Update_ResolvesRelativeLocalPathsFromApplicationDirectory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        var root = Path.Combine("runtime-path-tests", Guid.NewGuid().ToString("N"));
        var downloadPath = Path.Combine(root, "downloads");
        var copyPath = Path.Combine(root, "copies");
        var executablePath = Path.Combine(root, "tools", "complete.exe");
        var inboxPath = Path.Combine(root, "watch");
        var processedPath = Path.Combine(root, "processed");
        var errorPath = Path.Combine(root, "errors");
        const string reportedPath = "client/downloads";

        await settingData.Update([
            new SettingProperty { Key = "Storage:DownloadPath", Value = downloadPath },
            new SettingProperty { Key = "Integrations:AddedTorrentCopyPath", Value = copyPath },
            new SettingProperty { Key = "Integrations:CompletionCommand:ExecutablePath", Value = executablePath },
            new SettingProperty { Key = "WatchFolder:InboxPath", Value = inboxPath },
            new SettingProperty { Key = "WatchFolder:ProcessedPath", Value = processedPath },
            new SettingProperty { Key = "WatchFolder:ErrorPath", Value = errorPath },
            new SettingProperty { Key = "Integrations:ReportedDownloadPath", Value = reportedPath }
        ]);

        Assert.Equal(Path.GetFullPath(downloadPath, AppContext.BaseDirectory), SettingData.Get.Storage.DownloadPath);
        Assert.Equal(Path.GetFullPath(copyPath, AppContext.BaseDirectory), SettingData.Get.Integrations.AddedTorrentCopyPath);
        Assert.Equal(
            Path.GetFullPath(executablePath, AppContext.BaseDirectory),
            SettingData.Get.Integrations.CompletionCommand.ExecutablePath);
        Assert.Equal(Path.GetFullPath(inboxPath, AppContext.BaseDirectory), SettingData.Get.WatchFolder.InboxPath);
        Assert.Equal(Path.GetFullPath(processedPath, AppContext.BaseDirectory), SettingData.Get.WatchFolder.ProcessedPath);
        Assert.Equal(Path.GetFullPath(errorPath, AppContext.BaseDirectory), SettingData.Get.WatchFolder.ErrorPath);
        Assert.Equal(reportedPath, SettingData.Get.Integrations.ReportedDownloadPath);

        var persisted = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);
        Assert.Equal(Path.GetFullPath(downloadPath, AppContext.BaseDirectory), persisted["Storage:DownloadPath"].Value);
        Assert.Equal(reportedPath, persisted["Integrations:ReportedDownloadPath"].Value);
    }

    [Fact]
    public async Task Update_PreservesAbsoluteLocalPaths()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        var absolutePath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "adbclient-absolute-path-tests",
            Guid.NewGuid().ToString("N")));

        await settingData.Update([
            new SettingProperty { Key = "Storage:DownloadPath", Value = absolutePath },
            new SettingProperty { Key = "Integrations:AddedTorrentCopyPath", Value = absolutePath },
            new SettingProperty { Key = "Integrations:CompletionCommand:ExecutablePath", Value = absolutePath },
            new SettingProperty { Key = "WatchFolder:InboxPath", Value = absolutePath },
            new SettingProperty { Key = "WatchFolder:ProcessedPath", Value = absolutePath },
            new SettingProperty { Key = "WatchFolder:ErrorPath", Value = absolutePath }
        ]);

        Assert.Equal(absolutePath, SettingData.Get.Storage.DownloadPath);
        Assert.Equal(absolutePath, SettingData.Get.Integrations.AddedTorrentCopyPath);
        Assert.Equal(absolutePath, SettingData.Get.Integrations.CompletionCommand.ExecutablePath);
        Assert.Equal(absolutePath, SettingData.Get.WatchFolder.InboxPath);
        Assert.Equal(absolutePath, SettingData.Get.WatchFolder.ProcessedPath);
        Assert.Equal(absolutePath, SettingData.Get.WatchFolder.ErrorPath);
    }
}
