using AdbClient.Data.Models.Internal;
using Microsoft.Data.Sqlite;

namespace AdbClient.Service.Test.Data;

public class AppSettingsTest
{
    [Fact]
    public void NormalizeAndValidate_DefaultsAreAbsoluteAndSelfContained()
    {
        var workingDirectory = NewAbsolutePath("working");
        var settings = new AppSettings { DataPath = "state" };

        settings.NormalizeAndValidate(workingDirectory);

        var expectedDataPath = Path.GetFullPath("state", workingDirectory);
        Assert.Equal(expectedDataPath, settings.DataPath);
        Assert.Equal(AppSettings.DefaultPort, settings.Port);
        Assert.Null(settings.BasePath);
        Assert.Equal(Path.Combine(expectedDataPath, "adbclient.db"), settings.Database!.Path);
        Assert.Equal(Path.Combine(expectedDataPath, "adbclient.log"), settings.Logging!.File!.Path);
        Assert.Equal(AppSettingsLoggingFile.DefaultFileSizeLimitBytes,
                     settings.Logging.File.FileSizeLimitBytes);
        Assert.Equal(AppSettingsLoggingFile.DefaultMaxRollingFiles,
                     settings.Logging.File.MaxRollingFiles);
    }

    [Fact]
    public void NormalizeAndValidate_PreservesWorkingDirectoryRelativeOverrides()
    {
        var workingDirectory = NewAbsolutePath("working");
        var settings = new AppSettings
        {
            DataPath = "state",
            Database = new AppSettingsDatabase { Path = Path.Combine("database", "custom.db") },
            Logging = new AppSettingsLogging
            {
                File = new AppSettingsLoggingFile { Path = Path.Combine("logs", "custom.log") }
            }
        };

        settings.NormalizeAndValidate(workingDirectory);

        Assert.Equal(Path.Combine(workingDirectory, "database", "custom.db"), settings.Database.Path);
        Assert.Equal(Path.Combine(workingDirectory, "logs", "custom.log"), settings.Logging.File.Path);
        Assert.Equal(AppSettingsLoggingFile.DefaultFileSizeLimitBytes,
                     settings.Logging.File.FileSizeLimitBytes);
        Assert.Equal(AppSettingsLoggingFile.DefaultMaxRollingFiles,
                     settings.Logging.File.MaxRollingFiles);
    }

    [Fact]
    public void NormalizeAndValidate_PreservesExplicitAbsoluteFilePaths()
    {
        var databasePath = NewAbsolutePath("database", "custom.db");
        var logPath = NewAbsolutePath("logs", "custom.log");
        var settings = new AppSettings
        {
            DataPath = NewAbsolutePath("state"),
            Database = new AppSettingsDatabase { Path = databasePath },
            Logging = new AppSettingsLogging
            {
                File = new AppSettingsLoggingFile { Path = logPath }
            }
        };

        settings.NormalizeAndValidate(NewAbsolutePath("app"));

        Assert.Equal(databasePath, settings.Database.Path);
        Assert.Equal(logPath, settings.Logging.File.Path);
    }

    [Fact]
    public void NormalizeAndValidate_RejectsSharedDatabaseAndLogPath()
    {
        var settings = ValidSettings();
        settings.Database = new AppSettingsDatabase { Path = Path.Combine("shared", "app.data") };
        settings.Logging = new AppSettingsLogging
        {
            File = new AppSettingsLoggingFile { Path = Path.Combine("shared", ".", "app.data") }
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => settings.NormalizeAndValidate(NewAbsolutePath("app")));

        Assert.Contains("must identify different files", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65_536)]
    public void NormalizeAndValidate_RejectsInvalidPort(int port)
    {
        var settings = ValidSettings();
        settings.Port = port;

        var exception = Assert.Throws<InvalidOperationException>(
            () => settings.NormalizeAndValidate(NewAbsolutePath("app")));

        Assert.Contains("Port", exception.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65_535)]
    public void NormalizeAndValidate_AcceptsPortBoundary(int port)
    {
        var settings = ValidSettings();
        settings.Port = port;

        settings.NormalizeAndValidate(NewAbsolutePath("app"));

        Assert.Equal(port, settings.Port);
    }

    [Fact]
    public void NormalizeAndValidate_RejectsBlankDataPath()
    {
        var settings = ValidSettings();
        settings.DataPath = "   ";

        var exception = Assert.Throws<InvalidOperationException>(
            () => settings.NormalizeAndValidate(NewAbsolutePath("app")));

        Assert.Contains("DataPath", exception.Message);
    }

    [Theory]
    [InlineData("../admin")]
    [InlineData("admin//nested")]
    [InlineData("admin?debug=true")]
    [InlineData("admin\\nested")]
    [InlineData("https://example.com")]
    public void NormalizeAndValidate_RejectsUnsafeBasePath(string basePath)
    {
        var settings = ValidSettings();
        settings.BasePath = basePath;

        var exception = Assert.Throws<InvalidOperationException>(
            () => settings.NormalizeAndValidate(NewAbsolutePath("app")));

        Assert.Contains("BasePath", exception.Message);
    }

    [Fact]
    public void NormalizeAndValidate_NormalizesConfiguredBasePath()
    {
        var settings = ValidSettings();
        settings.BasePath = " /proxy/nested/ ";

        settings.NormalizeAndValidate(NewAbsolutePath("app"), "legacy");

        Assert.Equal("proxy/nested", settings.BasePath);
    }

    [Fact]
    public void NormalizeAndValidate_UsesLegacyBasePathWhenSettingIsBlank()
    {
        var settings = ValidSettings();
        settings.BasePath = " ";

        settings.NormalizeAndValidate(NewAbsolutePath("app"), "/legacy/");

        Assert.Equal("legacy", settings.BasePath);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(1024, 0)]
    [InlineData(1024, -1)]
    public void NormalizeAndValidate_RejectsNonPositiveLogRotation(long sizeLimit, int retainedFiles)
    {
        var settings = ValidSettings();
        settings.Logging = new AppSettingsLogging
        {
            File = new AppSettingsLoggingFile
            {
                FileSizeLimitBytes = sizeLimit,
                MaxRollingFiles = retainedFiles
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => settings.NormalizeAndValidate(NewAbsolutePath("app")));

        Assert.Contains("Logging:File", exception.Message);
    }

    [Theory]
    [InlineData("Database")]
    [InlineData("Logging")]
    public void NormalizeAndValidate_PreservesExplicitParentRelativePaths(string setting)
    {
        var settings = ValidSettings();
        var escapingPath = Path.Combine("..", "outside.db");

        if (setting == "Database")
        {
            settings.Database = new AppSettingsDatabase { Path = escapingPath };
        }
        else
        {
            settings.Logging = new AppSettingsLogging
            {
                File = new AppSettingsLoggingFile { Path = escapingPath }
            };
        }

        var workingDirectory = NewAbsolutePath("app");
        settings.NormalizeAndValidate(workingDirectory);

        var actual = setting == "Database" ? settings.Database!.Path : settings.Logging!.File!.Path;
        Assert.Equal(Path.GetFullPath(escapingPath, workingDirectory), actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalizeAndValidate_UpgradeReopensExistingRelativeDatabase(bool explicitOverride)
    {
        // Keep the fixture on the working directory's drive so the path is relative on Windows too.
        var fixtureName = $"adbclient-upgrade-test-{Guid.NewGuid():N}";
        var fixtureRoot = Path.Combine(Environment.CurrentDirectory, fixtureName);
        var relativeDataPath = Path.Combine(fixtureName, "state");
        var legacyDatabasePath = explicitOverride
            ? Path.Combine(fixtureName, "custom.db")
            : Path.Combine(relativeDataPath, "adbclient.db");
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "state"));

        try
        {
            await using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder
                         { DataSource = legacyDatabasePath, Pooling = false }.ToString()))
            {
                await legacy.OpenAsync();
                await using var command = legacy.CreateCommand();
                command.CommandText = "CREATE TABLE UpgradeSentinel (Value TEXT); INSERT INTO UpgradeSentinel VALUES ('existing data');";
                await command.ExecuteNonQueryAsync();
            }

            var settings = new AppSettings
            {
                DataPath = relativeDataPath,
                Database = new AppSettingsDatabase { Path = explicitOverride ? legacyDatabasePath : null }
            };
            settings.NormalizeAndValidate(Environment.CurrentDirectory);
            Assert.Equal(Path.GetFullPath(legacyDatabasePath), settings.Database.Path);
            Assert.Equal(Path.GetFullPath(relativeDataPath), settings.DataPath);

            // A repeated normalization must not rebase already captured absolute paths.
            settings.NormalizeAndValidate(Path.Combine(fixtureRoot, "different-working-directory"));
            Assert.Equal(Path.GetFullPath(legacyDatabasePath), settings.Database.Path);

            await using var upgraded = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = settings.Database.Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await upgraded.OpenAsync();
            await using var read = upgraded.CreateCommand();
            read.CommandText = "SELECT Value FROM UpgradeSentinel";
            Assert.Equal("existing data", await read.ExecuteScalarAsync());
            Assert.Single(Directory.GetFiles(fixtureRoot, "*.db", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(".")]
    [InlineData("logs/")]
    public void NormalizeAndValidate_RejectsDirectoryAsFilePath(string path)
    {
        var settings = ValidSettings();
        settings.Database = new AppSettingsDatabase { Path = path };

        var exception = Assert.Throws<InvalidOperationException>(
            () => settings.NormalizeAndValidate(NewAbsolutePath("app")));

        Assert.Contains("file, not a directory", exception.Message);
    }

    private static AppSettings ValidSettings()
    {
        return new AppSettings { DataPath = NewAbsolutePath("state") };
    }

    private static string NewAbsolutePath(params string[] components)
    {
        return Path.GetFullPath(Path.Combine([Path.GetTempPath(), "adbclient-settings-tests", .. components]));
    }
}
