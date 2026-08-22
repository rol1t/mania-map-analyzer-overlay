using Avalonia;
using ManiaMapAnalyzerOverlay.Avalonia.Platform;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class OverlayDragSessionTests
{
    [Theory]
    [InlineData(760, 100, 1, 760)]
    [InlineData(760, 100, 1.25, 950)]
    [InlineData(760, 125, 1, 950)]
    [InlineData(760, 125, 1.25, 1188)]
    public void UsesOnePhysicalSizeModel(double baseWidth, int scalePercent, double renderScaling, int expectedWidth)
    {
        Assert.Equal(expectedWidth, OverlaySizingModel.PhysicalWidth(baseWidth, scalePercent, renderScaling));
    }

    [Theory]
    [InlineData(760, 760, 100)]
    [InlineData(950, 760, 125)]
    [InlineData(1187.5, 760, 156)]
    public void CalculatesScaleFromNativeClientWidth(double clientWidth, double baseWidth, int expectedScale)
    {
        Assert.Equal(expectedScale, OverlaySizingModel.ScalePercentFromClientWidth(clientWidth, baseWidth));
    }

    [Fact]
    public void CalculatesPositionFromOriginalScreenAnchorAndRenderScale()
    {
        var session = new OverlayDragSession();

        Assert.True(session.Start(1, 7, 0, 1000, 500, new PixelPoint(100, 200), 2));
        Assert.True(session.TryMove(1, 7, 1, 1015, 493, out var position));

        Assert.Equal(new PixelPoint(130, 186), position);
        Assert.True(session.IsActive);
    }

    [Fact]
    public void UsesScreenDeltaEvenWhenWindowPositionHasAlreadyMoved()
    {
        var session = new OverlayDragSession();

        Assert.True(session.Start(1, 7, 0, 1200, 700, new PixelPoint(300, 400), 1.5));
        // The browser window may have moved since the pointerdown. A screen
        // anchor remains stable; client coordinates would be contaminated by
        // that window movement.
        Assert.True(session.TryMove(1, 7, 1, 1240, 730, out var position));

        Assert.Equal(new PixelPoint(360, 445), position);
    }

    [Fact]
    public void KeepsFixedScalingAcrossMovesDespiteHypotheticalMonitorChange()
    {
        var session = new OverlayDragSession();

        // Start on a 200% monitor: delta 20 logical pixels => 40 physical.
        Assert.True(session.Start(1, 7, 0, 100, 100, new PixelPoint(500, 500), 2));
        Assert.True(session.TryMove(1, 7, 1, 120, 100, out var first));
        Assert.Equal(new PixelPoint(540, 500), first);

        // If the gesture dragged onto a 100% monitor and we re-read
        // RenderScaling == 1, the correct behavior is still to use the
        // Start scaling (2), not the current monitor scaling. Same logical
        // delta 30 from the original anchor must still map to 60 physical.
        Assert.True(session.TryMove(1, 7, 2, 130, 110, out var second));
        Assert.Equal(new PixelPoint(560, 520), second);
    }

    [Fact]
    public void StartRejectsInvalidRenderScaling()
    {
        var session = new OverlayDragSession();

        Assert.False(session.Start(1, 7, 0, 10, 20, new PixelPoint(100, 200), double.NaN));
        Assert.False(session.Start(1, 7, 0, 10, 20, new PixelPoint(100, 200), double.PositiveInfinity));
        Assert.False(session.Start(1, 7, 0, 10, 20, new PixelPoint(100, 200), 0));
        Assert.False(session.Start(1, 7, 0, 10, 20, new PixelPoint(100, 200), -1));
        Assert.False(session.IsActive);

        // Zero/negative and non-finite deltas are also rejected at Start.
        Assert.False(session.Start(1, 7, 0, double.NaN, 20, new PixelPoint(100, 200), 1));
        Assert.False(session.Start(1, 7, 0, 10, double.NegativeInfinity, new PixelPoint(100, 200), 1));
    }

    [Fact]
    public void IgnoresDuplicateOutOfOrderAndWrongPointerMessages()
    {
        var session = new OverlayDragSession();

        Assert.True(session.Start(1, 7, 0, 10, 20, new PixelPoint(100, 200), 1));
        Assert.False(session.Start(1, 7, 0, 12, 22, new PixelPoint(120, 220), 1));
        Assert.False(session.TryMove(1, 7, 0, 15, 20, out _));
        Assert.False(session.TryMove(1, 8, 1, 15, 20, out _));
        Assert.True(session.TryMove(1, 7, 2, 15, 20, out var position));
        Assert.Equal(new PixelPoint(105, 200), position);
        Assert.False(session.TryMove(1, 7, 1, 30, 20, out _));
    }

    [Fact]
    public void NewGestureInvalidatesMessagesFromPreviousGesture()
    {
        var session = new OverlayDragSession();

        Assert.True(session.Start(1, 7, 0, 10, 20, new PixelPoint(100, 200), 1));
        Assert.True(session.Start(2, 7, 0, 40, 50, new PixelPoint(300, 400), 1));
        Assert.False(session.TryMove(1, 7, 1, 20, 30, out _));
        Assert.True(session.TryMove(2, 7, 1, 45, 55, out var position));
        Assert.Equal(new PixelPoint(305, 405), position);
    }

    [Fact]
    public void EndRejectsStaleMessageAndStopsFurtherMoves()
    {
        var session = new OverlayDragSession();

        Assert.True(session.Start(1, 7, 0, 10, 20, new PixelPoint(100, 200), 1));
        Assert.True(session.TryMove(1, 7, 1, 11, 21, out _));
        Assert.False(session.End(1, 7, 0));
        Assert.True(session.IsActive);
        Assert.True(session.End(1, 7, 2));
        Assert.False(session.IsActive);
        Assert.False(session.TryMove(1, 7, 3, 12, 22, out _));
    }
}
