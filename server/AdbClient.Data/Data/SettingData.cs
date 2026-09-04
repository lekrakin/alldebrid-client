using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AdbClient.Data.Helpers;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdbClient.Data.Data;

public class SettingData(DataContext dataContext, ILogger<SettingData> logger)
{
    private static readonly IReadOnlyDictionary<string, string> LegacySettingKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DownloadClient:DownloadPath"] = "Paths:DownloadPath",
            ["DownloadClient:MappedPath"] = "Paths:MappedPath",
            ["Provider:Default:Category"] = "DownloadClient:Default:Category",
            ["Provider:Default:MinFileSize"] = "DownloadClient:Default:MinFileSize",
            ["Provider:Default:TorrentRetryAttempts"] = "DownloadClient:Default:TorrentRetryAttempts",
            ["Provider:Default:DownloadRetryAttempts"] = "DownloadClient:Default:DownloadRetryAttempts",
            ["Provider:Default:DeleteOnError"] = "DownloadClient:Default:DeleteOnError",
            ["Provider:Default:TorrentLifetime"] = "DownloadClient:Default:TorrentLifetime",
        };

    private static DbSettings _current = new();

    public static DbSettings Get => Volatile.Read(ref _current);

    public static IList<SettingProperty> GetAll()
    {
        return GetSettings(Get, null);
    }

    public async Task Update(IList<SettingProperty> settings)
    {
        if (settings.Count == 0)
        {
            return;
        }

        var duplicateKey = settings.GroupBy(setting => setting.Key, StringComparer.Ordinal)
                                   .FirstOrDefault(group => group.Count() > 1)?.Key;

        if (duplicateKey != null)
        {
            throw new ArgumentException($"Setting '{duplicateKey}' was submitted more than once.", nameof(settings));
        }

        var definitions = GetSettings(new DbSettings(), null)
                         .Where(setting => setting.Type != "Object")
                         .ToDictionary(setting => setting.Key, StringComparer.Ordinal);
        var dbSettings = await dataContext.Settings.ToListAsync();
        var dbSettingsByKey = dbSettings.ToDictionary(setting => setting.SettingId, StringComparer.Ordinal);

        foreach (var setting in settings)
        {
            if (string.IsNullOrWhiteSpace(setting.Key) || !definitions.ContainsKey(setting.Key))
            {
                throw new ArgumentException($"Unknown setting '{setting.Key}'.", nameof(settings));
            }

            if (!dbSettingsByKey.TryGetValue(setting.Key, out var dbSetting))
            {
                throw new InvalidOperationException($"Setting '{setting.Key}' has not been initialized.");
            }

            try
            {
                dbSetting.Value = ConvertSubmittedValue(setting.Value);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid value for setting '{setting.Key}': {ex.Message}", nameof(settings), ex);
            }
        }

        var updatedSettings = Materialize(dbSettings, rejectInvalidValues: true);
        WriteCanonicalValues(dbSettings, updatedSettings);

        await dataContext.SaveChangesAsync();
        Volatile.Write(ref _current, updatedSettings);
    }

    public Task Update(string settingId, Object? value)
    {
        return Update([
            new SettingProperty
            {
                Key = settingId,
                Value = value
            }
        ]);
    }

    public async Task ResetCache()
    {
        var settings = await dataContext.Settings.AsNoTracking().ToListAsync();

        if (settings.Count == 0)
        {
            throw new InvalidOperationException("No settings found; restart AllDebrid Client to initialize them.");
        }

        Volatile.Write(ref _current, Materialize(settings, rejectInvalidValues: false));
    }

    public async Task Seed()
    {
        var dbSettings = await dataContext.Settings.ToListAsync();
        MigrateLegacySettingKeys(dbSettings);
        NormalizePathDefaults(dbSettings);

        var expectedSettings = GetSettings(new DbSettings(), null)
                              .Where(setting => setting.Type != "Object")
                              .Select(setting => new Setting
                              {
                                  SettingId = setting.Key,
                                  Value = ConvertToStorageValue(setting.Value)
                              })
                              .ToList();

        var newSettings = expectedSettings.Where(expected =>
            dbSettings.All(existing => existing.SettingId != expected.SettingId)).ToList();

        if (newSettings.Count > 0)
        {
            await dataContext.Settings.AddRangeAsync(newSettings);
            dbSettings.AddRange(newSettings);
        }

        var oldSettings = dbSettings.Where(existing =>
            expectedSettings.All(expected => expected.SettingId != existing.SettingId)).ToList();

        if (oldSettings.Count > 0)
        {
            dataContext.Settings.RemoveRange(oldSettings);

            foreach (var oldSetting in oldSettings)
            {
                dbSettings.Remove(oldSetting);
            }
        }

        var normalizedSettings = Materialize(dbSettings, rejectInvalidValues: false);
        WriteCanonicalValues(dbSettings, normalizedSettings);

        if (dataContext.ChangeTracker.HasChanges())
        {
            await dataContext.SaveChangesAsync();
        }
    }

    private DbSettings Materialize(IList<Setting> settings, bool rejectInvalidValues)
    {
        var result = new DbSettings();
        SetSettings(settings, result, null, rejectInvalidValues);
        NormalizeAndValidate(result, rejectInvalidValues);
        return result;
    }

    private void NormalizeAndValidate(DbSettings settings, bool rejectInvalidValues)
    {
        settings.Integrations.Categories = NormalizeCategories(
            "General:Categories",
            settings.Integrations.Categories,
            rejectInvalidValues);
        settings.Provider.BannedTrackers = NormalizeList(settings.Provider.BannedTrackers);
        settings.Downloads.Defaults.Category = NormalizeCategory(
            "DownloadClient:Default:Category",
            settings.Downloads.Defaults.Category,
            rejectInvalidValues);
        settings.Downloads.Defaults.IncludeRegex = ValidateRegex(
            "DownloadClient:Default:IncludeRegex",
            settings.Downloads.Defaults.IncludeRegex,
            rejectInvalidValues);
        settings.Downloads.Defaults.ExcludeRegex = ValidateRegex(
            "DownloadClient:Default:ExcludeRegex",
            settings.Downloads.Defaults.ExcludeRegex,
            rejectInvalidValues);
        settings.Provider.TrackerEnrichmentList = ValidateHttpUrl(
            "General:TrackerEnrichmentList",
            settings.Provider.TrackerEnrichmentList,
            rejectInvalidValues);
        settings.Storage.DownloadPath = NormalizeLocalPath(
            "Paths:DownloadPath",
            settings.Storage.DownloadPath,
            new DbSettingsStorage().DownloadPath,
            rejectInvalidValues)!;
        settings.Integrations.AddedTorrentCopyPath = NormalizeLocalPath(
            "Paths:CopyAddedTorrents",
            settings.Integrations.AddedTorrentCopyPath,
            null,
            rejectInvalidValues);
        settings.Integrations.CompletionCommand.ExecutablePath = NormalizeLocalPath(
            "General:RunOnTorrentCompleteFileName",
            settings.Integrations.CompletionCommand.ExecutablePath,
            null,
            rejectInvalidValues);
        settings.WatchFolder.InboxPath = NormalizeLocalPath(
            "Paths:WatchPath",
            settings.WatchFolder.InboxPath,
            null,
            rejectInvalidValues);
        settings.WatchFolder.ProcessedPath = NormalizeLocalPath(
            "Paths:WatchProcessedPath",
            settings.WatchFolder.ProcessedPath,
            null,
            rejectInvalidValues);
        settings.WatchFolder.ErrorPath = NormalizeLocalPath(
            "Paths:WatchErrorPath",
            settings.WatchFolder.ErrorPath,
            null,
            rejectInvalidValues);

        if (PathsAreEquivalent(settings.Storage.DownloadPath, settings.Integrations.ReportedDownloadPath))
        {
            settings.Integrations.ReportedDownloadPath = null;
        }
    }

    private void MigrateLegacySettingKeys(IList<Setting> settings)
    {
        foreach (var (legacyKey, currentKey) in LegacySettingKeys)
        {
            var legacySetting = settings.FirstOrDefault(setting => setting.SettingId == legacyKey);

            if (legacySetting == null)
            {
                continue;
            }

            if (settings.All(setting => setting.SettingId != currentKey))
            {
                var currentSetting = new Setting
                {
                    SettingId = currentKey,
                    Value = legacySetting.Value
                };
                dataContext.Settings.Add(currentSetting);
                settings.Add(currentSetting);
            }

            dataContext.Settings.Remove(legacySetting);
            settings.Remove(legacySetting);
        }
    }

    private string? NormalizeCategories(string key, string? value, bool rejectInvalidValues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var categories = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .Select(category => TorrentCategory.Normalize(category)!)
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .ToList();

            return categories.Count == 0 ? null : string.Join(',', categories);
        }
        catch (ArgumentException ex)
        {
            return HandleInvalidValue<string?>(key, ex.Message, null, rejectInvalidValues);
        }
    }

    private string? NormalizeCategory(string key, string? value, bool rejectInvalidValues)
    {
        try
        {
            return TorrentCategory.Normalize(value);
        }
        catch (ArgumentException ex)
        {
            return HandleInvalidValue<string?>(key, ex.Message, null, rejectInvalidValues);
        }
    }

    private string? ValidateRegex(string key, string? value, bool rejectInvalidValues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            _ = BoundedRegex.Create(value);
            return value;
        }
        catch (ArgumentException ex)
        {
            return HandleInvalidValue<string?>(key, ex.Message, null, rejectInvalidValues);
        }
    }

    private string? ValidateHttpUrl(string key, string? value, bool rejectInvalidValues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.AbsoluteUri;
        }

        return HandleInvalidValue<string?>(
            key,
            "Value must be an absolute HTTP or HTTPS URL.",
            null,
            rejectInvalidValues);
    }

    private string? NormalizeLocalPath(
        string key,
        string? value,
        string? defaultValue,
        bool rejectInvalidValues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var path = value.Trim();

        try
        {
            if (Path.IsPathRooted(path))
            {
                _ = Path.GetFullPath(path, AppContext.BaseDirectory);
                return path;
            }

            return Path.GetFullPath(path, AppContext.BaseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return HandleInvalidValue(
                key,
                "Value must be a valid local filesystem path.",
                defaultValue,
                rejectInvalidValues);
        }
    }

    private T HandleInvalidValue<T>(string key, string reason, T defaultValue, bool rejectInvalidValues)
    {
        if (rejectInvalidValues)
        {
            throw new ArgumentException($"Invalid value for setting '{key}': {reason}");
        }

        logger.LogWarning(
            "Replacing invalid stored value for setting {SettingKey} with its default: {Reason}",
            key,
            reason);
        return defaultValue;
    }

    private static string? NormalizeList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        return values.Count == 0 ? null : string.Join(',', values);
    }

    private static void WriteCanonicalValues(IEnumerable<Setting> dbSettings, DbSettings settings)
    {
        var values = GetSettings(settings, null)
                    .Where(setting => setting.Type != "Object")
                    .ToDictionary(
                        setting => setting.Key,
                        setting => ConvertToStorageValue(setting.Value),
                        StringComparer.Ordinal);

        foreach (var dbSetting in dbSettings)
        {
            if (values.TryGetValue(dbSetting.SettingId, out var value))
            {
                dbSetting.Value = value;
            }
        }
    }

    private static void NormalizePathDefaults(IList<Setting> settings)
    {
        const string legacyWindowsDefault = @"C:\Downloads";

        var downloadPath = settings.FirstOrDefault(setting => setting.SettingId == "Paths:DownloadPath");
        var reportedPath = settings.FirstOrDefault(setting =>
            setting.SettingId == "Paths:MappedPath");
        var reportedPathIsRedundant = downloadPath != null &&
                                      reportedPath != null &&
                                      PathsAreEquivalent(downloadPath.Value, reportedPath.Value);

        if (!OperatingSystem.IsWindows() &&
            downloadPath != null &&
            string.Equals(downloadPath.Value, legacyWindowsDefault, StringComparison.OrdinalIgnoreCase))
        {
            downloadPath.Value = "/data/downloads";
        }

        if (reportedPathIsRedundant)
        {
            reportedPath!.Value = null;
        }
    }

    private static bool PathsAreEquivalent(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        var normalizedFirst = first.Trim().TrimEnd('/', '\\');
        var normalizedSecond = second.Trim().TrimEnd('/', '\\');
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(normalizedFirst, normalizedSecond, comparison);
    }

    private static List<SettingProperty> GetSettings(Object defaultSetting, string? parent)
    {
        var result = new List<SettingProperty>();
        var properties = defaultSetting.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var displayName = property.GetCustomAttribute<DisplayNameAttribute>();
            var description = property.GetCustomAttribute<DescriptionAttribute>();
            var propertyName = parent == null ? property.Name : $"{parent}:{property.Name}";
            var range = property.GetCustomAttribute<RangeAttribute>();
            var dataType = property.GetCustomAttribute<DataTypeAttribute>();
            var settingProperty = new SettingProperty
            {
                Key = property.GetCustomAttribute<SettingKeyAttribute>()?.Key ?? propertyName,
                ParentKey = parent,
                DisplayName = displayName?.DisplayName,
                Description = description?.Description,
                Type = property.PropertyType.Name,
                Minimum = range == null ? null : Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture),
                Maximum = range == null ? null : Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture),
                IsSecret = dataType?.DataType == DataType.Password
            };

            if (property.PropertyType.IsEnum ||
                property.PropertyType.IsValueType ||
                property.PropertyType == typeof(string))
            {
                settingProperty.Value = property.GetValue(defaultSetting);

                if (property.PropertyType.IsEnum)
                {
                    settingProperty.Type = "Enum";
                    settingProperty.EnumValues = [];

                    foreach (var enumValue in Enum.GetValues(property.PropertyType).Cast<Enum>())
                    {
                        var enumMember = property.PropertyType.GetMember(enumValue.ToString()).First();
                        var enumDescription = enumMember.GetCustomAttribute<DescriptionAttribute>();
                        var enumName = enumDescription?.Description ??
                                       Enum.GetName(property.PropertyType, enumValue) ??
                                       "Unknown value";
                        settingProperty.EnumValues.Add((int)(Object)enumValue, enumName);
                    }
                }

                result.Add(settingProperty);
                continue;
            }

            settingProperty.Type = "Object";
            result.Add(settingProperty);
            result.AddRange(GetSettings(property.GetValue(defaultSetting)!, propertyName));
        }

        return result;
    }

    private void SetSettings(
        IList<Setting> settings,
        Object defaultSetting,
        string? parent,
        bool rejectInvalidValues)
    {
        var properties = defaultSetting.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var propertyName = parent == null ? property.Name : $"{parent}:{property.Name}";

            if (property.PropertyType.IsEnum ||
                property.PropertyType.IsValueType ||
                property.PropertyType == typeof(string))
            {
                var settingKey = property.GetCustomAttribute<SettingKeyAttribute>()?.Key ?? propertyName;
                var setting = settings.FirstOrDefault(item => item.SettingId == settingKey);

                if (setting == null)
                {
                    continue;
                }

                try
                {
                    var newValue = ConvertStoredValue(property, settingKey, setting.Value);
                    ValidateProperty(defaultSetting, property, newValue);
                    property.SetValue(defaultSetting, newValue);
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or NotSupportedException)
                {
                    if (rejectInvalidValues)
                    {
                        throw new ArgumentException(
                            $"Invalid value for setting '{settingKey}': {ex.Message}",
                            nameof(settings),
                            ex);
                    }

                    logger.LogWarning(
                        "Replacing invalid stored value for setting {SettingKey} with its default: {Reason}",
                        settingKey,
                        ex.Message);
                }

                continue;
            }

            SetSettings(
                settings,
                property.GetValue(defaultSetting)!,
                propertyName,
                rejectInvalidValues);
        }
    }

    private static object? ConvertStoredValue(PropertyInfo property, string key, string? value)
    {
        if (property.PropertyType == typeof(string))
        {
            if (key == "Provider:ApiKey")
            {
                return value?.Trim() ?? string.Empty;
            }

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        if (value == null)
        {
            throw new ArgumentException("Value is required.");
        }

        if (property.PropertyType.IsEnum)
        {
            if (!Enum.TryParse(property.PropertyType, value, ignoreCase: true, out var enumValue) ||
                enumValue == null ||
                !Enum.IsDefined(property.PropertyType, enumValue))
            {
                throw new ArgumentException($"'{value}' is not a supported option.");
            }

            return enumValue;
        }

        var converter = TypeDescriptor.GetConverter(property.PropertyType);

        if (!converter.CanConvertFrom(typeof(string)))
        {
            throw new NotSupportedException($"Values of type {property.PropertyType.Name} are not supported.");
        }

        return converter.ConvertFromInvariantString(value);
    }

    private static void ValidateProperty(object owner, PropertyInfo property, object? value)
    {
        var validationContext = new ValidationContext(owner)
        {
            MemberName = property.Name,
            DisplayName = property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? property.Name
        };
        var validationResults = new List<ValidationResult>();

        if (!Validator.TryValidateProperty(value, validationContext, validationResults))
        {
            throw new ArgumentException(validationResults[0].ErrorMessage ?? "Value is invalid.");
        }
    }

    private static string? ConvertSubmittedValue(object? value)
    {
        if (value is JsonElement jsonValue)
        {
            return jsonValue.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => jsonValue.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => jsonValue.ToString(),
                _ => throw new ArgumentException("Expected a string, number, boolean, or null.")
            };
        }

        return ConvertToStorageValue(value);
    }

    private static string? ConvertToStorageValue(object? value)
    {
        return value switch
        {
            null => null,
            Enum enumValue => Convert.ToInt32(enumValue, CultureInfo.InvariantCulture)
                                     .ToString(CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }
}
