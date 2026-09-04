using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdbClient.Data.Helpers;
using AdbClient.Data.Models.Data;
using AdbClient.Data.Models.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdbClient.Data.Data;

public class SettingData(DataContext dataContext, ILogger<SettingData> logger)
{
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

        NormalizeLegacyPathDefaults(dbSettings);

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
        settings.General.Categories = NormalizeCategories(
            "General:Categories",
            settings.General.Categories,
            rejectInvalidValues);
        settings.General.BannedTrackers = NormalizeList(settings.General.BannedTrackers);
        settings.DownloadClient.Default.Category = NormalizeCategory(
            "DownloadClient:Default:Category",
            settings.DownloadClient.Default.Category,
            rejectInvalidValues);
        settings.DownloadClient.Default.IncludeRegex = ValidateRegex(
            "DownloadClient:Default:IncludeRegex",
            settings.DownloadClient.Default.IncludeRegex,
            rejectInvalidValues);
        settings.DownloadClient.Default.ExcludeRegex = ValidateRegex(
            "DownloadClient:Default:ExcludeRegex",
            settings.DownloadClient.Default.ExcludeRegex,
            rejectInvalidValues);
        settings.General.TrackerEnrichmentList = ValidateHttpUrl(
            "General:TrackerEnrichmentList",
            settings.General.TrackerEnrichmentList,
            rejectInvalidValues);

        if (PathsAreEquivalent(settings.Paths.DownloadPath, settings.Paths.MappedPath))
        {
            settings.Paths.MappedPath = null;
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
            _ = new Regex(value, RegexOptions.None, TimeSpan.FromSeconds(1));
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

    private static void NormalizeLegacyPathDefaults(IList<Setting> settings)
    {
        const string legacyWindowsDefault = @"C:\Downloads";

        var downloadPath = settings.FirstOrDefault(setting => setting.SettingId == "Paths:DownloadPath");
        var reportedPath = settings.FirstOrDefault(setting => setting.SettingId == "Paths:MappedPath");

        if (!OperatingSystem.IsWindows() &&
            downloadPath != null &&
            string.Equals(downloadPath.Value, legacyWindowsDefault, StringComparison.OrdinalIgnoreCase))
        {
            downloadPath.Value = "/data/downloads";
        }

        if (downloadPath != null &&
            reportedPath != null &&
            PathsAreEquivalent(downloadPath.Value, reportedPath.Value))
        {
            reportedPath.Value = null;
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
                Key = propertyName,
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
                var setting = settings.FirstOrDefault(item => item.SettingId == propertyName);

                if (setting == null)
                {
                    continue;
                }

                try
                {
                    var newValue = ConvertStoredValue(property, propertyName, setting.Value);
                    ValidateProperty(defaultSetting, property, newValue);
                    property.SetValue(defaultSetting, newValue);
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or NotSupportedException)
                {
                    if (rejectInvalidValues)
                    {
                        throw new ArgumentException(
                            $"Invalid value for setting '{propertyName}': {ex.Message}",
                            nameof(settings),
                            ex);
                    }

                    logger.LogWarning(
                        "Replacing invalid stored value for setting {SettingKey} with its default: {Reason}",
                        propertyName,
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
