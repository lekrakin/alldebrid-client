namespace AdbClient.Data.Models.Internal;

public class AppSettings
{
    public const int DefaultPort = 6500;

    public string DataPath { get; set; } = "./data";
    public AppSettingsLogging? Logging { get; set; }
    public AppSettingsDatabase? Database { get; set; }

    public int Port { get; set; } = DefaultPort;
    public string? BasePath { get; set; }

    public void NormalizeAndValidate(string contentRootPath, string? legacyBasePath = null)
    {
        var normalizedContentRoot = NormalizeDirectoryPath(contentRootPath, null, nameof(contentRootPath));

        if (string.IsNullOrWhiteSpace(DataPath))
        {
            throw new InvalidOperationException("DataPath must not be blank.");
        }

        DataPath = NormalizeDirectoryPath(DataPath, normalizedContentRoot, nameof(DataPath));

        if (Port is < 1 or > 65_535)
        {
            throw new InvalidOperationException("Port must be between 1 and 65535.");
        }

        BasePath = NormalizeBasePath(!string.IsNullOrWhiteSpace(BasePath) ? BasePath : legacyBasePath);

        Database ??= new AppSettingsDatabase();
        Database.Path = NormalizeFilePath(Database.Path, DataPath, "Database:Path", "adbclient.db");

        Logging ??= new AppSettingsLogging();
        Logging.File ??= new AppSettingsLoggingFile();
        Logging.File.Path = NormalizeFilePath(Logging.File.Path, DataPath, "Logging:File:Path", "adbclient.log");

        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(Database.Path, Logging.File.Path, pathComparison))
        {
            throw new InvalidOperationException("Database:Path and Logging:File:Path must identify different files.");
        }

        if (Logging.File.FileSizeLimitBytes <= 0)
        {
            throw new InvalidOperationException("Logging:File:FileSizeLimitBytes must be greater than zero.");
        }

        if (Logging.File.MaxRollingFiles <= 0)
        {
            throw new InvalidOperationException("Logging:File:MaxRollingFiles must be greater than zero.");
        }
    }

    private static string NormalizeDirectoryPath(string path, string? relativeTo, string settingName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{settingName} must not be blank.");
        }

        try
        {
            var trimmedPath = path.Trim();
            var fullPath = Path.IsPathRooted(trimmedPath)
                ? Path.GetFullPath(trimmedPath)
                : Path.GetFullPath(trimmedPath, relativeTo ?? AppContext.BaseDirectory);

            return Path.TrimEndingDirectorySeparator(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"{settingName} is not a valid filesystem path.", ex);
        }
    }

    private static string NormalizeFilePath(string? path, string dataPath, string settingName, string defaultFileName)
    {
        var configuredPath = string.IsNullOrWhiteSpace(path) ? defaultFileName : path.Trim();

        try
        {
            var configuredFileName = Path.GetFileName(configuredPath);
            if (string.IsNullOrWhiteSpace(configuredFileName) || configuredFileName is "." or "..")
            {
                throw new InvalidOperationException($"{settingName} must identify a file, not a directory.");
            }

            var fullPath = Path.IsPathRooted(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : Path.GetFullPath(configuredPath, dataPath);

            if (!Path.IsPathRooted(configuredPath) && !IsWithinDirectory(fullPath, dataPath))
            {
                throw new InvalidOperationException($"Relative {settingName} must remain inside DataPath.");
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"{settingName} is not a valid filesystem path.", ex);
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relativePath = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relativePath)
               && relativePath != ".."
               && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string? NormalizeBasePath(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return null;
        }

        var normalized = basePath.Trim().Trim('/');
        if (normalized.Length == 0)
        {
            return null;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.Any(character => !IsUrlPathCharacter(character)))
            {
                throw new InvalidOperationException(
                    "BasePath must contain only URL-safe path segments separated by '/'.");
            }
        }

        return normalized;
    }

    private static bool IsUrlPathCharacter(char character)
    {
        return character is >= 'a' and <= 'z'
               or >= 'A' and <= 'Z'
               or >= '0' and <= '9'
               or '-' or '.' or '_' or '~';
    }
}

public class AppSettingsLogging
{
    public AppSettingsLoggingFile? File { get; set; }
}

public class AppSettingsLoggingFile
{
    public const long DefaultFileSizeLimitBytes = 5_242_880;
    public const int DefaultMaxRollingFiles = 5;

    public string? Path { get; set; }
    public long FileSizeLimitBytes { get; set; } = DefaultFileSizeLimitBytes;
    public int MaxRollingFiles { get; set; } = DefaultMaxRollingFiles;
}

public class AppSettingsDatabase
{
    public string? Path { get; set; }
}
