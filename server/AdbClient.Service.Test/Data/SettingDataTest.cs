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

        Assert.Equal(1, settings["General:DownloadLimit"].Minimum);
        Assert.Equal(16, settings["DownloadClient:ParallelCount"].Maximum);
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

        var downloadPath = SettingData.Get.Paths.DownloadPath;
        await settingData.Update([
            new SettingProperty { Key = "General:Categories", Value = " radarr,SONARR,radarr " },
            new SettingProperty { Key = "General:BannedTrackers", Value = " private,PRIVATE, internal " },
            new SettingProperty { Key = "Paths:MappedPath", Value = $"{downloadPath}/" },
            new SettingProperty { Key = "Provider:ApiKey", Value = "  secret-token  " }
        ]);

        Assert.Equal("radarr,SONARR", SettingData.Get.General.Categories);
        Assert.Equal("private,internal", SettingData.Get.General.BannedTrackers);
        Assert.Null(SettingData.Get.Paths.MappedPath);
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

        Assert.Equal(2, SettingData.Get.General.DownloadLimit);
        Assert.Null(SettingData.Get.DownloadClient.Default.IncludeRegex);
        Assert.Null(SettingData.Get.General.TrackerEnrichmentList);

        var persisted = await dataContext.Settings.AsNoTracking().ToDictionaryAsync(setting => setting.SettingId);
        Assert.Equal("2", persisted["General:DownloadLimit"].Value);
        Assert.Null(persisted["DownloadClient:Default:IncludeRegex"].Value);
        Assert.Null(persisted["General:TrackerEnrichmentList"].Value);
    }
}
