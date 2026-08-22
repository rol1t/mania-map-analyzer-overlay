using Avalonia;
using ManiaMapAnalyzerOverlay.Avalonia.Platform;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class OverlayResizeSessionTests
{
    [Fact]
    public void ResizesBottomRightFromOriginalScreenAnchor()
    {
        var session = new OverlayResizeSession();

        Assert.True(session.Start(
            1, 7, 0, "se", 100, 200, new PixelPoint(300, 400), new Size(760, 340), 1.5, 120, 80, new Size(1400, 900)));
        Assert.True(session.TryMove(1, 7, 1, 140, 220, out var position, out var size));

        Assert.Equal(new PixelPoint(300, 400), position);
        Assert.Equal(new Size(800, 360), size);
    }

    [Fact]
    public void ResizesTopLeftAndMovesWindowOrigin()
    {
        var session = new OverlayResizeSession();

        Assert.True(session.Start(
            1, 7, 0, "nw", 500, 500, new PixelPoint(200, 300), new Size(400, 240), 2, 120, 80, new Size(1400, 900)));
        Assert.True(session.TryMove(1, 7, 1, 510, 490, out var position, out var size));

        Assert.Equal(new PixelPoint(220, 280), position);
        Assert.Equal(new Size(390, 250), size);
    }

    [Fact]
    public void ClampsMovingEdgesToMinimumClientSize()
    {
        var session = new OverlayResizeSession();

        Assert.True(session.Start(
            1, 7, 0, "nw", 0, 0, new PixelPoint(100, 200), new Size(200, 100), 1, 120, 80, new Size(1400, 900)));
        Assert.True(session.TryMove(1, 7, 1, 500, 500, out var position, out var size));

        Assert.Equal(new PixelPoint(180, 220), position);
        Assert.Equal(new Size(120, 80), size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("center")]
    [InlineData("north-east")]
    public void RejectsUnknownDirection(string direction)
    {
        var session = new OverlayResizeSession();

        Assert.False(session.Start(
            1, 7, 0, direction, 0, 0, new PixelPoint(100, 200), new Size(200, 100), 1, 120, 80, new Size(1400, 900)));
    }

    [Fact]
    public void RejectsStaleMessagesAndStopsAfterEnd()
    {
        var session = new OverlayResizeSession();

        Assert.True(session.Start(
            1, 7, 0, "e", 0, 0, new PixelPoint(100, 200), new Size(200, 100), 1, 120, 80, new Size(1400, 900)));
        Assert.True(session.TryMove(1, 7, 2, 10, 0, out _, out _));
        Assert.False(session.TryMove(1, 7, 1, 20, 0, out _, out _));
        Assert.False(session.End(1, 7, 1));
        Assert.True(session.End(1, 7, 3));
        Assert.False(session.TryMove(1, 7, 4, 30, 0, out _, out _));
    }

    [Fact]
    public void ClampsGrowingEdgeToMaximumClientSize()
    {
        var session = new OverlayResizeSession();

        Assert.True(session.Start(
            1, 7, 0, "se", 0, 0, new PixelPoint(100, 200), new Size(200, 100), 1, 120, 80, new Size(500, 300)));
        Assert.True(session.TryMove(1, 7, 1, 1000, 1000, out var position, out var size));

        Assert.Equal(new PixelPoint(100, 200), position);
        Assert.Equal(new Size(500, 300), size);
    }
}
