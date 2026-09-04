using AdbClient.Data.Data;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.Internal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace AdbClient.Service.Test.Data;

[Collection(SettingsIsolationCollection.Name)]
public class SettingDataTest
{
    [Fact]
    public void GetAll_SeparatesDisplayHierarchyFromPersistedKeys()
    {
        var settings = SettingData.GetAll().ToDictionary(setting => setting.Key);

        Assert.Null(settings["Downloads"].ParentKey);
        Assert.Equal("Downloads", settings["Downloads:Defaults"].ParentKey);
        Assert.Equal("Downloads", settings["General:DownloadLimit"].ParentKey);
        Assert.Equal("Downloads:Defaults", settings["DownloadClient:Default:FinishedAction"].ParentKey);
        Assert.Equal("Storage", settings["Paths:DownloadPath"].ParentKey);
        Assert.Equal("Integrations", settings["Paths:MappedPath"].ParentKey);
        Assert.Equal("Integrations:CompletionCommand", settings["General:RunOnTorrentCompleteFileName"].ParentKey);
        Assert.Equal("Provider", settings["DownloadClient:AutoDelete"].ParentKey);
        Assert.Equal("WatchFolder", settings["Paths:WatchPath"].ParentKey);

        var tabs = settings.Values.Where(setting => setting.Type == "Object" && setting.ParentKey == null).ToList();
        foreach (var setting in settings.Values.Where(setting => setting.Type != "Object"))
        {
            Assert.Single(tabs, tab => setting.ParentKey == tab.Key ||
                setting.ParentKey!.StartsWith($"{tab.Key}:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void GetAll_ExposesInputConstraintsAndSecretMetadata()
    {
        var settings = SettingData.GetAll().ToDictionary(setting => setting.Key);

        Assert.Equal(1, settings["General:DownloadLimit"].Minimum);
        Assert.Equal(16, settings["DownloadClient:ParallelCount"].Maximum);
        Assert.Equal(1, settings["Integrations:CompletionCommand:TimeoutSeconds"].Minimum);
        Assert.Equal(3600, settings["Integrations:CompletionCommand:TimeoutSeconds"].Maximum);
        Assert.True(settings["Provider:ApiKey"].IsSecret);
        Assert.False(settings["Paths:DownloadPath"].IsSecret);
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

        Assert.Equal(expectedDownloadPath, settings["Paths:DownloadPath"].Value);
        Assert.Null(settings["Paths:MappedPath"].Value);

        await settingData.ResetCache();
        Assert.Equal(AdbClient.Data.Enums.LogLevel.Warning, SettingData.Get.General.LogLevel);
        Assert.Equal(AdbClient.Data.Enums.TorrentFinishedAction.None, SettingData.Get.Downloads.Defaults.FinishedAction);

    }

    [Fact]
    public async Task Seed_PreservesReleasedSettingKeysAndValuesAcrossRestarts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DataContext>()
                     .UseSqlite(connection)
                     .Options;
        await using var dataContext = new DataContext(options);
        await dataContext.Database.EnsureCreatedAsync();

        (string Key, string Value)[] releasedSettings =
        [
            ("General:LogLevel", "2"),
            ("General:AuthenticationType", "1"),
            ("General:DisableUpdateNotifications", "True"),
            ("Provider:ApiKey", "test-provider-key"),
            ("Provider:Timeout", "35"),
            ("Provider:CheckInterval", "15"),
            ("General:DownloadLimit", "4"),
            ("General:UnpackLimit", "2"),
            ("General:Categories", "radarr,sonarr"),
            ("General:RunOnTorrentCompleteFileName", Path.GetFullPath("/tools/finish.exe")),
            ("General:RunOnTorrentCompleteArguments", "%N --path %F"),
            ("General:TrackerEnrichmentList", "https://example.com/trackers.txt"),
            ("General:TrackerEnrichmentCacheExpiration", "120"),
            ("General:BannedTrackers", "private,internal"),
            ("DownloadClient:MaxSpeed", "25"),
            ("DownloadClient:ParallelCount", "4"),
            ("DownloadClient:ParallelChunkCount", "16"),
            ("DownloadClient:AutoImport", "True"),
            ("DownloadClient:AutoDelete", "True"),
            ("DownloadClient:MaxParallelDownloads", "3"),
            ("DownloadClient:Default:HostDownloadAction", "0"),
            ("DownloadClient:Default:Category", "radarr"),
            ("DownloadClient:Default:FinishedAction", "0"),
            ("DownloadClient:Default:FinishedActionDelay", "15"),
            ("DownloadClient:Default:MinFileSize", "10"),
            ("DownloadClient:Default:IncludeRegex", @"\.mkv$"),
            ("DownloadClient:Default:ExcludeRegex", @"\.txt$"),
            ("DownloadClient:Default:TorrentRetryAttempts", "5"),
            ("DownloadClient:Default:DownloadRetryAttempts", "6"),
            ("DownloadClient:Default:DeleteOnError", "30"),
            ("DownloadClient:Default:TorrentLifetime", "1440"),
            ("DownloadClient:Default:Priority", "7"),
            ("Paths:DownloadPath", "/srv/downloads"),
            ("Paths:MappedPath", "/media/downloads"),
            ("Paths:CopyAddedTorrents", "/srv/torrent-copies"),
            ("Paths:WatchPath", "/srv/watch"),
            ("Paths:WatchErrorPath", "/srv/watch-errors"),
            ("Paths:WatchProcessedPath", "/srv/watch-processed"),
            ("Watch:Interval", "90")
        ];

        dataContext.Settings.AddRange(releasedSettings.Select(setting => new Setting
        {
            SettingId = setting.Key,
            Value = setting.Value
        }));
        await dataContext.SaveChangesAsync();
        dataContext.ChangeTracker.Clear();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        await settingData.ResetCache();
        await settingData.Update(SettingData.GetAll().Where(setting => setting.Type != "Object").ToList());
        dataContext.ChangeTracker.Clear();
        await settingData.Seed();
        await settingData.ResetCache();

        var persisted = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);
        var exposed = SettingData.GetAll().Where(setting => setting.Type != "Object")
            .Select(setting => setting.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var setting in releasedSettings)
        {
            Assert.Equal(setting.Value, persisted[setting.Key].Value);
            Assert.Contains(setting.Key, exposed);
        }

        Assert.Equal(releasedSettings.Length + 1, persisted.Count);
        Assert.Equal(persisted.Keys.Order(), exposed.Order());
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
            ("DownloadClient:DownloadPath", "Paths:DownloadPath", "/srv/direct-upgrade-downloads"),
            ("DownloadClient:MappedPath", "Paths:MappedPath", "/media/direct-upgrade-downloads"),
            ("Provider:Default:Category", "DownloadClient:Default:Category", "provider-import"),
            ("Provider:Default:MinFileSize", "DownloadClient:Default:MinFileSize", "20"),
            ("Provider:Default:TorrentRetryAttempts", "DownloadClient:Default:TorrentRetryAttempts", "8"),
            ("Provider:Default:DownloadRetryAttempts", "DownloadClient:Default:DownloadRetryAttempts", "9"),
            ("Provider:Default:DeleteOnError", "DownloadClient:Default:DeleteOnError", "45"),
            ("Provider:Default:TorrentLifetime", "DownloadClient:Default:TorrentLifetime", "2880")
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
            new Setting { SettingId = "Provider:Default:Category", Value = "legacy" },
            new Setting { SettingId = "DownloadClient:Default:Category", Value = "radarr" });
        await dataContext.SaveChangesAsync();
        dataContext.ChangeTracker.Clear();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();

        var settings = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);

        Assert.DoesNotContain("Provider:Default:Category", settings.Keys);
        Assert.Equal("radarr", settings["DownloadClient:Default:Category"].Value);
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

        Assert.Equal(downloadPath, settings["Paths:DownloadPath"].Value);
        Assert.Null(settings["Paths:MappedPath"].Value);
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

        Assert.Equal("/data/downloads", settings["Paths:DownloadPath"].Value);
        Assert.Null(settings["Paths:MappedPath"].Value);
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
            setting.SettingId == "DownloadClient:ParallelChunkCount" && setting.Value == "0");
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
            new SettingProperty { Key = "General:Categories", Value = " radarr,SONARR,radarr " },
            new SettingProperty { Key = "General:BannedTrackers", Value = " private,PRIVATE, internal " },
            new SettingProperty { Key = "Paths:MappedPath", Value = $"{downloadPath}/" },
            new SettingProperty { Key = "Provider:ApiKey", Value = "  secret-token  " }
        ]);

        Assert.Equal("radarr,SONARR", SettingData.Get.Integrations.Categories);
        Assert.Equal("private,internal", SettingData.Get.Provider.BannedTrackers);
        Assert.Null(SettingData.Get.Integrations.ReportedDownloadPath);
        Assert.Equal("secret-token", SettingData.Get.Provider.ApiKey);

        var persisted = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);
        Assert.Equal("radarr,SONARR", persisted["General:Categories"].Value);
        Assert.Equal("private,internal", persisted["General:BannedTrackers"].Value);
        Assert.Null(persisted["Paths:MappedPath"].Value);
        Assert.Equal("secret-token", persisted["Provider:ApiKey"].Value);
    }

    [Theory]
    [InlineData("General:DownloadLimit", -1)]
    [InlineData("DownloadClient:ParallelCount", 17)]
    [InlineData("Provider:CheckInterval", 4)]
    [InlineData("DownloadClient:Default:TorrentRetryAttempts", 1001)]
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
    [InlineData("DownloadClient:Default:IncludeRegex", "[")]
    [InlineData("General:TrackerEnrichmentList", "file:///trackers.txt")]
    [InlineData("DownloadClient:Default:Category", "../outside")]
    [InlineData("General:Categories", "radarr,../outside")]
    [InlineData("Paths:DownloadPath", "invalid\0path")]
    [InlineData("Paths:WatchPath", "invalid\0path")]
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
                new SettingProperty { Key = "General:DownloadLimit", Value = 2 },
                new SettingProperty { Key = "General:DownloadLimit", Value = 3 }
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
            new Setting { SettingId = "General:DownloadLimit", Value = "0" },
            new Setting { SettingId = "DownloadClient:Default:IncludeRegex", Value = "[" },
            new Setting { SettingId = "General:TrackerEnrichmentList", Value = "not-a-url" });
        await dataContext.SaveChangesAsync();
        dataContext.ChangeTracker.Clear();

        var settingData = new SettingData(dataContext, Mock.Of<ILogger<SettingData>>());
        await settingData.Seed();
        await settingData.ResetCache();

        Assert.Equal(2, SettingData.Get.Downloads.ConcurrentFiles);
        Assert.Null(SettingData.Get.Downloads.Defaults.IncludeRegex);
        Assert.Null(SettingData.Get.Provider.TrackerEnrichmentList);

        var persisted = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);
        Assert.Equal("2", persisted["General:DownloadLimit"].Value);
        Assert.Null(persisted["DownloadClient:Default:IncludeRegex"].Value);
        Assert.Null(persisted["General:TrackerEnrichmentList"].Value);
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
            new SettingProperty { Key = "Paths:DownloadPath", Value = downloadPath },
            new SettingProperty { Key = "Paths:CopyAddedTorrents", Value = copyPath },
            new SettingProperty { Key = "General:RunOnTorrentCompleteFileName", Value = executablePath },
            new SettingProperty { Key = "Paths:WatchPath", Value = inboxPath },
            new SettingProperty { Key = "Paths:WatchProcessedPath", Value = processedPath },
            new SettingProperty { Key = "Paths:WatchErrorPath", Value = errorPath },
            new SettingProperty { Key = "Paths:MappedPath", Value = reportedPath }
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
        Assert.Equal(Path.GetFullPath(downloadPath, AppContext.BaseDirectory), persisted["Paths:DownloadPath"].Value);
        Assert.Equal(reportedPath, persisted["Paths:MappedPath"].Value);
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
            new SettingProperty { Key = "Paths:DownloadPath", Value = absolutePath },
            new SettingProperty { Key = "Paths:CopyAddedTorrents", Value = absolutePath },
            new SettingProperty { Key = "General:RunOnTorrentCompleteFileName", Value = absolutePath },
            new SettingProperty { Key = "Paths:WatchPath", Value = absolutePath },
            new SettingProperty { Key = "Paths:WatchProcessedPath", Value = absolutePath },
            new SettingProperty { Key = "Paths:WatchErrorPath", Value = absolutePath }
        ]);

        Assert.Equal(absolutePath, SettingData.Get.Storage.DownloadPath);
        Assert.Equal(absolutePath, SettingData.Get.Integrations.AddedTorrentCopyPath);
        Assert.Equal(absolutePath, SettingData.Get.Integrations.CompletionCommand.ExecutablePath);
        Assert.Equal(absolutePath, SettingData.Get.WatchFolder.InboxPath);
        Assert.Equal(absolutePath, SettingData.Get.WatchFolder.ProcessedPath);
        Assert.Equal(absolutePath, SettingData.Get.WatchFolder.ErrorPath);
    }
}
