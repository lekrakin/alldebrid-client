namespace AdbClient.Data.Models.Internal;

public class SettingProperty
{
    public string Key { get; set; } = default!;
    public string? ParentKey { get; set; }
    public Object? Value { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string Type { get; set; } = default!;
    public Dictionary<int, string>? EnumValues { get; set; }
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public bool IsSecret { get; set; }
}
