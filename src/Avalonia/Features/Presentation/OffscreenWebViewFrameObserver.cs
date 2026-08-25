using System;
using System.Reflection;
using Avalonia.Platform;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Presentation;

/// <summary>
/// Observes the frame-request heartbeat exposed by the experimental Windows
/// offscreen WebView adapter. Avalonia.Controls.WebView 11.4.1 does not expose
/// this event through its public platform-handle interface, so the adapter
/// event is accessed defensively and only for the known composition handle.
/// </summary>
public sealed class OffscreenWebViewFrameObserver : IDisposable
{
    private const string CompositionHandleDescriptor = "Windows.UI.Composition.ContainerVisual";
    private readonly Action _frameObserved;
    private readonly Action<Exception>? _observerFailed;
    private readonly Action _handler;
    private object? _adapter;
    private MethodInfo? _removeMethod;

    public OffscreenWebViewFrameObserver(
        Action frameObserved,
        Action<Exception>? observerFailed = null)
    {
        _frameObserved = frameObserved ?? throw new ArgumentNullException(nameof(frameObserved));
        _observerFailed = observerFailed;
        _handler = OnFrameRequested;
    }

    public bool IsAttached => _adapter is not null;

    public bool Attach(IPlatformHandle? platformHandle)
    {
        Detach();
        if (platformHandle is null ||
            !string.Equals(
                platformHandle.HandleDescriptor,
                CompositionHandleDescriptor,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var eventInfo = platformHandle.GetType().GetEvent(
                "DrawRequested",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var addMethod = eventInfo?.GetAddMethod(nonPublic: true);
            var removeMethod = eventInfo?.GetRemoveMethod(nonPublic: true);
            if (eventInfo?.EventHandlerType != typeof(Action) || addMethod is null || removeMethod is null)
            {
                return false;
            }

            addMethod.Invoke(platformHandle, [_handler]);
            _adapter = platformHandle;
            _removeMethod = removeMethod;
            return true;
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
            return false;
        }
    }

    public void Detach(IPlatformHandle? expectedAdapter = null)
    {
        if (_adapter is null ||
            (expectedAdapter is not null && !ReferenceEquals(expectedAdapter, _adapter)))
        {
            return;
        }

        try
        {
            _removeMethod?.Invoke(_adapter, [_handler]);
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
        finally
        {
            _adapter = null;
            _removeMethod = null;
        }
    }

    public void Dispose()
    {
        Detach();
        GC.SuppressFinalize(this);
    }

    private void OnFrameRequested() => _frameObserved();

    private void ReportFailure(Exception exception)
    {
        try
        {
            _observerFailed?.Invoke(exception);
        }
        catch
        {
            // Diagnostics must not break WebView adapter creation/destruction.
        }
    }
}
