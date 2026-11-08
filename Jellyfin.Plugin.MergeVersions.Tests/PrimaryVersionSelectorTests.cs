using Xunit;

namespace Jellyfin.Plugin.MergeVersions.Tests;

public class PrimaryVersionSelectorTests
{
    private const string Order = PrimaryVersionSelector.DefaultOrder;

    [Theory]
    [InlineData(3840, "2160")]
    [InlineData(2560, "2160")]
    [InlineData(1920, "1080")]
    [InlineData(1440, "1080")]
    [InlineData(1280, "720")]
    [InlineData(720, "SD")]
    public void ClassifiesByWidth(int width, string expected)
    {
        Assert.Equal(expected, PrimaryVersionSelector.Classify(width));
    }

    [Fact]
    public void PrefersFullHdOverOtherVersions()
    {
        Assert.Equal(1, PrimaryVersionSelector.SelectIndex([3840, 1920, 1280], Order));
    }

    [Fact]
    public void FallsBackTo720WhenFullHdIsMissing()
    {
        Assert.Equal(1, PrimaryVersionSelector.SelectIndex([3840, 1280], Order));
    }

    [Fact]
    public void FallsBackToUhdBeforeSd()
    {
        Assert.Equal(1, PrimaryVersionSelector.SelectIndex([720, 3840], Order));
    }

    [Fact]
    public void PicksWidestWithinTheSameClass()
    {
        Assert.Equal(1, PrimaryVersionSelector.SelectIndex([1440, 1920], Order));
    }

    [Fact]
    public void CustomOrderWinsAndAcceptsSuffixes()
    {
        Assert.Equal(0, PrimaryVersionSelector.SelectIndex([3840, 1920], "2160p, 1080p"));
    }

    [Fact]
    public void ClassMissingFromOrderStillPicksAVersion()
    {
        Assert.Equal(1, PrimaryVersionSelector.SelectIndex([720, 3840], "1080"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")]
    public void EmptyOrderUsesDefault(string? order)
    {
        Assert.Equal(1, PrimaryVersionSelector.SelectIndex([3840, 1920], order!));
    }

    [Fact]
    public void WaitsWhileAWidthIsUnknown()
    {
        Assert.Equal(-1, PrimaryVersionSelector.SelectIndex([0, 1920], Order));
    }

    [Fact]
    public void PrimaryBecomesNewerThanLaterVersion()
    {
        var primary = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var later = primary.AddDays(1);
        Assert.Equal(later.AddSeconds(1), PrimaryVersionSelector.GetPrimaryDateCreated(primary, [later]));
    }

    [Fact]
    public void PrimaryDateStaysWhenItIsAlreadyNewest()
    {
        var primary = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(primary, PrimaryVersionSelector.GetPrimaryDateCreated(primary, [primary.AddDays(-1)]));
    }

    [Fact]
    public void EqualDatesStillMakePrimaryNewer()
    {
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(date.AddSeconds(1), PrimaryVersionSelector.GetPrimaryDateCreated(date, [date]));
    }

    [Fact]
    public void EmptyListSelectsNothing()
    {
        Assert.Equal(-1, PrimaryVersionSelector.SelectIndex([], Order));
    }
}
