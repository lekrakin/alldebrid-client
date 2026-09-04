using AdbClient.Data.Models.Internal;

namespace AdbClient.Service.Test.Data;

public class AppSettingsTest
{
    [Fact]
    public void NormalizeAndValidate_DefaultsAreAbsoluteAndSelfContained()
    {
        var contentRoot = NewAbsolutePath("app");
        var settings = new AppSettings { DataPath = "state" };

        settings.NormalizeAndValidate(contentRoot);

        var expectedDataPath = Path.GetFullPath("state", contentRoot);
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
    public void NormalizeAndValidate_ResolvesRelativeFilesFromDataPath()
    {
        var contentRoot = NewAbsolutePath("app");
        var settings = new AppSettings
        {
            DataPath = "state",
            Database = new AppSettingsDatabase { Path = Path.Combine("database", "custom.db") },
            Logging = new AppSettingsLogging
            {
                File = new AppSettingsLoggingFile { Path = Path.Combine("logs", "custom.log") }
            }
        };

        settings.NormalizeAndValidate(contentRoot);

        Assert.Equal(Path.Combine(settings.DataPath, "database", "custom.db"), settings.Database.Path);
        Assert.Equal(Path.Combine(settings.DataPath, "logs", "custom.log"), settings.Logging.File.Path);
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
    public void NormalizeAndValidate_RejectsRelativeFileOutsideDataPath(string setting)
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

        var exception = Assert.Throws<InvalidOperationException>(
            () => settings.NormalizeAndValidate(NewAbsolutePath("app")));

        Assert.Contains("DataPath", exception.Message);
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
