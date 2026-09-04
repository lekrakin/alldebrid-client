using AdbClient.Data.Helpers;

namespace AdbClient.Service.Test.Helpers;

public class BoundedRegexTest
{
    [Fact]
    public void Create_UsesSharedMatchTimeout()
    {
        var regex = BoundedRegex.Create("file");

        Assert.Equal(TimeSpan.FromSeconds(1), regex.MatchTimeout);
    }

    [Fact]
    public void Create_InvalidPattern_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => BoundedRegex.Create("["));
    }
}
