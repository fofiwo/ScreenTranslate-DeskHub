using System.Drawing;
using OverTranslate.Services.ClipboardHistory;
using Xunit;

namespace OverTranslate.Tests;

public sealed class ClipboardPopupPlacementTests
{
    [Theory]
    [InlineData(1.0, 12)]
    [InlineData(1.5, 18)]
    [InlineData(2.0, 0)]
    public void DefaultUsesBottomRightOfWorkAreaIncludingNegativeMonitorCoordinates(double scale, int expectedBottomGap)
    {
        var work = new Rectangle(-2560, -120, 2560, 1400);
        var result = ClipboardPopupPlacement.Place(work, scale, 560, 700, null);
        Assert.True(work.Contains(result));
        Assert.Equal(work.Right - (int)Math.Round(12 * scale), result.Right);
        Assert.Equal(work.Bottom - expectedBottomGap, result.Bottom);
    }

    [Fact]
    public void SavedBoundsAreClampedAfterDisplayChangesAndSmallWorkAreasRemainUsable()
    {
        var work = new Rectangle(1920, 0, 800, 580);
        var saved = new ClipboardWindowPlacement(-8000, 9000, 1000, 1000, "fixture");
        var result = ClipboardPopupPlacement.Place(work, 1.5, saved.Width, saved.Height, saved);
        Assert.Equal(work, result);
        var corrupt = ClipboardPopupPlacement.Place(work, 1.5, double.NaN, double.PositiveInfinity, saved);
        Assert.True(work.Contains(corrupt));
    }
}
