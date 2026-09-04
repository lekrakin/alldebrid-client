using System.Text.RegularExpressions;

namespace AdbClient.Data.Helpers;

public static class BoundedRegex
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    public const string TimeoutError = "Regular expression evaluation exceeded the one-second safety limit.";

    public static Regex Create(string pattern)
    {
        return new(pattern, RegexOptions.None, MatchTimeout);
    }

    public static bool IsMatch(string input, string pattern)
    {
        return Regex.IsMatch(input, pattern, RegexOptions.None, MatchTimeout);
    }
}
