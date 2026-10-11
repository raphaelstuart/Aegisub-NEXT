using AegiNext.Desktop.Updates;
using AegiNext.Desktop.Views;

namespace AegiNext.Desktop.Tests.Updates;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("rel/beta/0.7.0.0", "0.7.0.0")]
    [InlineData("rel/stable/1.2.3", "1.2.3.0")]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2.3", "1.2.3.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    public void ReleaseTagsNormalizeToFourNumericComponents(string text, string expected)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new Version(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.3-beta")]
    [InlineData("release 1.2.3")]
    [InlineData("rel//1.2.3")]
    [InlineData("rel/beta/extra/1.2.3")]
    [InlineData("1.2.-3")]
    [InlineData("1.2.2147483648")]
    [InlineData(" 1.2.3 ")]
    public void UnrecognizedVersionsAreRejected(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out var version));
        Assert.Null(version);
    }

    [Fact]
    public void AboutUsesTheSameAssemblyVersionAsUpdateChecks()
    {
        Assert.Equal(ApplicationVersion.Current, new AboutViewModel().Version);
        Assert.True(ReleaseVersion.TryParse(ApplicationVersion.Current, out _));
    }
}
