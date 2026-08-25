using System;
using Avalonia.Platform;
using ManiaMapAnalyzerOverlay.Avalonia.Features.Presentation;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class OffscreenWebViewFrameObserverTests
{
    [Fact]
    public void CompositionAdapterHeartbeatIsObservedUntilDetached()
    {
        var frames = 0;
        var adapter = new FakeCompositionAdapter();
        using var observer = new OffscreenWebViewFrameObserver(() => frames++);

        Assert.True(observer.Attach(adapter));
        adapter.RaiseDrawRequested();
        Assert.Equal(1, frames);

        observer.Detach(adapter);
        adapter.RaiseDrawRequested();
        Assert.Equal(1, frames);
    }

    [Fact]
    public void OrdinaryChildWindowAdapterIsIgnored()
    {
        var adapter = new FakeChildWindowAdapter();
        using var observer = new OffscreenWebViewFrameObserver(
            () => throw new InvalidOperationException("must not observe HWND frames"));

        Assert.False(observer.Attach(adapter));
        Assert.False(observer.IsAttached);
    }

    private sealed class FakeCompositionAdapter : IPlatformHandle
    {
        public IntPtr Handle => new(1);
        public string HandleDescriptor => "Windows.UI.Composition.ContainerVisual";
        public event Action? DrawRequested;

        public void RaiseDrawRequested() => DrawRequested?.Invoke();
    }

    private sealed class FakeChildWindowAdapter : IPlatformHandle
    {
        public IntPtr Handle => new(2);
        public string HandleDescriptor => "HWND";
    }
}
