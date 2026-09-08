namespace AdbClient.Data.Models.Internal;

// Persisted keys are independent of the settings model's display hierarchy.
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingKeyAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}
