using AdbClient.Data.Data;
using AdbClient.Data.Enums;
using AdbClient.Data.Models.Internal;
using Serilog.Core;
using Serilog.Events;

namespace AdbClient.Service.Services;

public class Settings(SettingData settingData)
{
    public static readonly LoggingLevelSwitch LoggingLevelSwitch = new(LogEventLevel.Debug);

    public static DbSettings Get => SettingData.Get;

    public async Task Update(IList<SettingProperty> settings)
    {
        await settingData.Update(settings);
        ApplyRuntimeSettings();
    }

    public async Task Update(string settingId, Object? value)
    {
        await settingData.Update(settingId, value);
        ApplyRuntimeSettings();
    }

    public async Task Seed()
    {
        await settingData.Seed();
    }

    public async Task ResetCache()
    {
        await settingData.ResetCache();

        ApplyRuntimeSettings();
    }

    private static void ApplyRuntimeSettings()
    {
        LoggingLevelSwitch.MinimumLevel = Settings.Get.General.LogLevel switch
        {
            LogLevel.Verbose => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            _ => LogEventLevel.Warning
        };
    }
}
