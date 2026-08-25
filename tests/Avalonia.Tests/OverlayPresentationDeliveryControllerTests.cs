using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Avalonia.Features.Presentation;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class OverlayPresentationDeliveryControllerTests
{
    [Fact]
    public void DesktopSurfaceGenerationAdvancesBeforeEachNewSession()
    {
        var controller = CreateController();

        Assert.Equal(0, controller.DesktopSurfaceGeneration);
        Assert.Equal(1, controller.BeginDesktopPresentationSession());
        Assert.Equal(1, controller.DesktopSurfaceGeneration);
        Assert.Equal(2, controller.BeginDesktopPresentationSession());
        Assert.Equal(2, controller.DesktopSurfaceGeneration);
    }

    [Fact]
    public void FullscreenSurfaceGenerationAdvancesForEachNewPresentationDocument()
    {
        var controller = CreateController();

        Assert.Equal(0, controller.FullscreenSurfaceGeneration);

        controller.SetFullscreenPresentationEnabled(true);
        Assert.Equal(1, controller.FullscreenSurfaceGeneration);

        controller.SetFullscreenPresentationEnabled(false);
        Assert.Equal(1, controller.FullscreenSurfaceGeneration);

        controller.SetFullscreenPresentationEnabled(true);
        Assert.Equal(2, controller.FullscreenSurfaceGeneration);
    }

    private static OverlayPresentationDeliveryController CreateController()
    {
        return new OverlayPresentationDeliveryController(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => { });
    }
}
