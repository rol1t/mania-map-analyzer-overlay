using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using ManiaMapAnalyzerOverlay.Avalonia.Services;

namespace ManiaMapAnalyzerOverlay.Avalonia.Platform;

public sealed class WindowsOverlayController : IDisposable
{
    private const int ExitHotkeyId = 0x4D41;
    private const int InputHotkeyId = 0x4D42;
    private const uint WmHotkey = 0x0312;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmMouseActivate = 0x0021;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExLayered = 0x00080000L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtClient = 0x0001;
    private const int HtCaption = 0x0002;
    private const int MaNoActivateAndEat = 0x0004;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private readonly Window _window;
    private readonly Win32Properties.CustomWndProcHookCallback _callback;
    private readonly DispatcherTimer _guardTimer;
    private bool _registered;
    private bool _overlayMode;
    private bool _clickThrough;
    private bool _interactive;
    private bool _osuFocused;
    private bool _osuMinimized;
    private bool _cursorHiddenForOsu;
    private bool? _osuProcessRunning;
    private IntPtr _hotkeyHandle;

    public WindowsOverlayController(Window window)
    {
        _window = window;
        _callback = WndProc;
        Win32Properties.AddWndProcHookCallback(_window, _callback);
        _guardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _guardTimer.Tick += (_, _) =>
        {
            try
            {
                SynchronizeWithOsuWindow();
            }
            catch (Exception exception) { AppLogger.Error("Synchronizing overlay with osu!", exception); }

            try
            {
                SynchronizeChildWindowTransparency();
            }
            catch (Exception exception) { AppLogger.Error("Synchronizing child window transparency", exception, userVisible: false); }
        };
    }

    public event EventHandler? ExitRequested;
    public event Action<bool>? ClickThroughChanged;
    public event Action<bool>? InteractionChanged;
    public event Action<bool>? OsuProcessChanged;
    public event Action<bool>? OsuWindowMinimizedChanged;
    public bool IsSupported => OperatingSystem.IsWindows();
    public bool IsClickThrough => _clickThrough;
    public bool IsOsuFocused => _osuFocused;
    public bool IsOsuMinimized => _osuMinimized;
    public bool IsInteractionAllowed =>
        _overlayMode && _interactive && !_clickThrough && !IsOsuInteractionBlocked();

    public bool IsWindowShown
    {
        get
        {
            var handle = Handle;
            return IsSupported && handle != IntPtr.Zero && IsWindowVisible(handle);
        }
    }

    public bool RegisterHotkeys()
    {
        if (!IsSupported || _registered)
        {
            return IsSupported;
        }

        var handle = Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var exit = RegisterHotKey(handle, ExitHotkeyId, ModControl | ModShift, VkF10);
        var input = RegisterHotKey(handle, InputHotkeyId, ModControl | ModShift, VkF9);
        _registered = exit && input;
        _hotkeyHandle = _registered ? handle : IntPtr.Zero;
        return _registered;
    }

    public void Enter()
    {
        // Capture the foreground state before applying WS_EX_NOACTIVATE or
        // disabling this HWND. If the launcher owns the foreground because the
        // user just clicked Overlay, disabling it first transfers focus back to
        // osu! and makes the edit surface immediately lock itself.
        var osuWasForeground = IsForegroundOsuProcess();
        _overlayMode = true;
        // A new overlay session must not inherit a stale websocket focus
        // signal from a previous session. The native foreground-process
        // check below remains the safety net until the watcher reports the
        // current value.
        _osuFocused = false;
        _osuProcessRunning = null;
        SetClickThrough(osuWasForeground);
        _guardTimer.Start();
        // Do not synchronously transfer focus after disabling the overlay
        // HWND. Avalonia's WebView2 adapter can still be processing a focus
        // transition at this point; SetForegroundWindow then re-enters the
        // adapter and can terminate the UI thread with an ArgumentException.
        // The guard below performs the safety synchronization without that
        // unsafe focus transfer, while click-through remains enabled now.
        SynchronizeWithOsuWindow();
    }

    public void ReapplyNativeState(bool visible)
    {
        if (!IsSupported)
        {
            return;
        }

        var handle = Handle;
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The overlay window handle is not available after changing window properties.");
        }

        if (_registered && _hotkeyHandle != handle)
        {
            UnregisterHotKey(_hotkeyHandle, ExitHotkeyId);
            UnregisterHotKey(_hotkeyHandle, InputHotkeyId);
            _registered = false;
        }

        if (!_registered)
        {
            _registered = RegisterHotKey(handle, ExitHotkeyId, ModControl | ModShift, VkF10) &&
                          RegisterHotKey(handle, InputHotkeyId, ModControl | ModShift, VkF9);
            _hotkeyHandle = handle;
        }

        ApplyStyles();
        SynchronizeChildWindowTransparency();
        ShowWindow(handle, visible ? SwShowNoActivate : SwHide);
    }

    public void Leave()
    {
        _guardTimer.Stop();
        SetCursorHiddenForOsu(false);
        _osuFocused = false;
        SetOsuMinimized(false);
        _osuProcessRunning = null;
        var handle = Handle;
        // Protected overlay mode disables the top-level _window. Re-enable it
        // before changing mode so Avalonia/WebView can be used normally again.
        if (IsSupported && handle != IntPtr.Zero)
        {
            EnableWindow(handle, true);
        }

        _overlayMode = false;
        var clickThroughWasEnabled = _clickThrough;
        SetClickThrough(false);
        SetInteractive(false);
        ApplyStyles();
        if (!clickThroughWasEnabled)
        {
            // SetClickThrough is intentionally idempotent and therefore does
            // not raise its event when the flag was already false. Still
            // notify the WebView host so a stale IsHitTestVisible=false state
            // cannot survive an osu! process exit.
            ClickThroughChanged?.Invoke(false);
        }
        if (IsSupported && handle != IntPtr.Zero)
        {
            EnableWindow(handle, true);
        }
    }

    public void ToggleInput()
    {
        if (!_overlayMode)
        {
            return;
        }

        var state = IsSupported ? GetOsuWindowState() : OsuWindowState.Unknown;
        if (state == OsuWindowState.Minimized)
        {
            // Minimized osu! is the automatic editing mode. Clear any stale
            // websocket focus signal before unlocking so the hotkey cannot
            // leave the WebView disabled or click-through while the game is
            // minimized.
            _osuFocused = false;
            SetOsuMinimized(true);
            SetClickThrough(false);
            return;
        }

        if (IsOsuInteractionBlocked())
        {
            ProtectForOsu();
            return;
        }
        if (_clickThrough && state == OsuWindowState.Restored)
        {
            ReturnFocusToOsu();
            return;
        }
        SetClickThrough(!_clickThrough);
        if (_clickThrough && state == OsuWindowState.Restored)
        {
            ReturnFocusToOsu();
        }
    }

    public void BeginDrag()
    {
        if (!_overlayMode || !_interactive || _clickThrough || IsOsuInteractionBlocked() || Handle == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        // Do not synchronously re-enter the Avalonia/WebView HWND from its
        // pointer callback. Posting the non-client message lets the current
        // WebView dispatch return first, which avoids intermittent native
        // crashes when dragging starts over a hosted child HWND.
        if (!PostMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero))
        {
            AppLogger.Warning("Starting overlay drag", "Windows rejected the native drag message.");
        }
    }

    public void SetClickThrough(bool enabled)
    {
        // A hotkey or a stale UI _callback must not unlock the overlay while
        // osu! is the active user _window. The guard will reevaluate this when
        // focus changes or the foreground process changes.
        if (!enabled && _overlayMode && IsOsuInteractionBlocked())
        {
            enabled = true;
        }

        if (_clickThrough == enabled)
        {
            ApplyStyles();
            SetInteractive(_overlayMode && !_clickThrough && !IsOsuInteractionBlocked());
            return;
        }
        _clickThrough = enabled;
        ApplyStyles();
        ClickThroughChanged?.Invoke(enabled);
        SetInteractive(_overlayMode && !_clickThrough && !IsOsuInteractionBlocked());
    }

    public void SetOsuFocused(bool focused)
    {
        if (focused && IsSupported && GetOsuWindowState() == OsuWindowState.Minimized)
        {
            // The browser focus callback can arrive after Windows has already
            // minimized osu!. Do not let that stale callback re-protect the
            // overlay or make its native WebView child transparent.
            focused = false;
            SetOsuMinimized(true);
        }

        _osuFocused = focused;
        if (!_overlayMode)
        {
            return;
        }

        if (focused)
        {
            // Apply protection synchronously: the 250 ms guard interval is
            // intentionally not part of the focus-signal safety path.
            ProtectForOsu();
            return;
        }

        // A focus=false message releases the websocket-side protection. The
        // native foreground-process check remains the final safety net while
        // the game is still the active user _window.
        SynchronizeWithOsuWindow();
    }

    public void SetWindowVisible(bool visible)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Native overlay visibility is only available on Windows.");
        }

        var handle = Handle;
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The overlay _window handle is not available.");
        }

        ShowWindow(handle, visible ? SwShowNoActivate : SwHide);
        if (IsWindowVisible(handle) != visible)
        {
            throw new InvalidOperationException(
                visible
                    ? "Windows did not show the overlay _window."
                    : "Windows did not hide the overlay _window.");
        }
    }

    private void ProtectForOsu()
    {
        if (!_overlayMode)
        {
            return;
        }

        SetCursorHiddenForOsu(true);
        SetClickThrough(true);
        SetInteractive(false);
        ApplyStyles();
    }

    private void ApplyStyles()
    {
        if (!IsSupported || Handle == IntPtr.Zero)
        {
            return;
        }

        var styles = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        var originalStyles = styles;
        var protectedInput = _overlayMode && (_clickThrough || _osuFocused || IsForegroundOsuProcess());
        if (_overlayMode)
        {
            if (protectedInput)
            {
                styles |= WsExToolWindow | WsExNoActivate;
                if (_clickThrough)
                {
                    styles |= WsExTransparent;
                }
                else
                {
                    styles &= ~WsExTransparent;
                }
            }
            else
            {
                // A NativeWebView is backed by child HWNDs. Keeping the parent
                // WS_EX_NOACTIVATE/WS_EX_TOOLWINDOW while editing prevents some
                // WebView2 versions from receiving the first pointer message at
                // all. Make the safe edit state a normal activatable window; the
                // protected osu! state above restores both styles immediately.
                styles &= ~(WsExToolWindow | WsExNoActivate | WsExTransparent);
            }
        }
        else
        {
            styles &= ~(WsExToolWindow | WsExNoActivate | WsExTransparent | WsExLayered);
        }
        if (styles != originalStyles)
        {
            SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(styles));
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        // Do not route protected clicks through the overlay. A disabled
        // top-level _window keeps both Avalonia and WebView child HWNDs from
        // receiving the click, while still leaving the overlay visible.
        // EnableWindow does not unregister RegisterHotKey bindings; WM_HOTKEY
        // remains dispatched to this _window's hook while it is disabled.
        EnableWindow(Handle, !protectedInput);
        SynchronizeChildWindowTransparency();
    }

    private void SynchronizeChildWindowTransparency()
    {
        if (!IsSupported)
        {
            return;
        }

        var parent = Handle;
        if (parent == IntPtr.Zero || !IsWindow(parent))
        {
            return;
        }

        try
        {
            EnumWindowsProc? callback = null;
            callback = (childHandle, _) =>
            {
                try
                {
                    if (childHandle == IntPtr.Zero || !IsWindow(childHandle))
                    {
                        return true;
                    }

                    var exStyle = GetWindowLongPtr(childHandle, GwlExStyle).ToInt64();
                    var desired = exStyle;
                    // Do not apply WS_EX_TRANSPARENT to a WebView2 descendant.
                    // On affected WebView2 versions it makes the child surface
                    // stop painting altogether, not merely pass mouse input
                    // through. The disabled parent HWND and the top-level
                    // click-through style already protect osu! from input.
                    desired &= ~WsExTransparent;

                    if (desired != exStyle)
                    {
                        SetWindowLongPtr(childHandle, GwlExStyle, new IntPtr(desired));
                    }

                    try
                    {
                        EnumChildWindows(childHandle, callback!, IntPtr.Zero);
                    }
                    catch (Exception exception)
                    {
                        AppLogger.Error("Enumerating descendant windows for transparency", exception, userVisible: false);
                    }
                }
                catch (Exception exception)
                {
                    AppLogger.Error("Synchronizing child window transparency", exception, userVisible: false);
                }

                return true;
            };

            EnumChildWindows(parent, callback, IntPtr.Zero);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Enumerating child windows for transparency", exception, userVisible: false);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            // The protected top-level _window is disabled in ApplyStyles, but keep
            // the native hit-test inside this _window if Windows asks for one. In
            // particular, never return HTTRANSPARENT: that can route a click to
            // osu! or another _window underneath the overlay.
            // Keep the native frame out of the input path. The WebView child
            // owns the visible client area; overlay resizing is intentionally
            // unavailable through the Windows frame or mouse edge handles.
            if (message == WmNcHitTest && _overlayMode)
            {
                handled = true;
                return (IntPtr)HtClient;
            }
            if (message == WmMouseActivate && _overlayMode && _clickThrough)
            {
                handled = true;
                return (IntPtr)MaNoActivateAndEat;
            }
            if (message != WmHotkey)
            {
                return IntPtr.Zero;
            }

            if (wParam.ToInt32() == ExitHotkeyId)
            {
                handled = true;
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (wParam.ToInt32() == InputHotkeyId)
            {
                handled = true;
                ToggleInput();
            }
            return IntPtr.Zero;
        }
        catch (Exception exception)
        {
            // This _callback is entered from an unmanaged Win32 _window
            // procedure. Letting an exception escape it terminates the CLR with
            // 0xC000041D instead of reaching Avalonia's normal error handling.
            handled = true;
            AppLogger.Error($"Processing overlay _window message 0x{message:X}", exception);
            return IntPtr.Zero;
        }
    }

    private void SynchronizeWithOsuWindow()
    {
        if (!_overlayMode || !IsSupported)
        {
            return;
        }

        var processRunning = IsOsuProcessRunning();
        if (_osuProcessRunning != processRunning)
        {
            _osuProcessRunning = processRunning;
            try
            {
                OsuProcessChanged?.Invoke(processRunning);
            }
            catch (Exception exception) { AppLogger.Error("Reporting osu! process state", exception, userVisible: false); }
        }

        // The overlay belongs to the game session. Once osu! exits there is no
        // safe foreground _window to protect against, so return control to the
        // normal launcher instead of leaving a detached widget on screen.
        if (!processRunning)
        {
            SetCursorHiddenForOsu(false);
            SetOsuMinimized(false);
            SetClickThrough(false);
            return;
        }

        var windowState = GetOsuWindowState();
        var minimized = windowState == OsuWindowState.Minimized;
        SetOsuMinimized(minimized);
        if (minimized)
        {
            SetCursorHiddenForOsu(false);
            // A minimized game must never keep the overlay in protected
            // click-through mode. In particular, clear a delayed websocket
            // focus signal before applying native styles so the WebView stays
            // painted and interactive.
            _osuFocused = false;
            SetClickThrough(false);
            return;
        }

        // The websocket focus signal and the native foreground-process check
        // are independent protections. Either one is sufficient to keep the
        // top-level HWND disabled, even when osu! is fullscreen/borderless
        // and its IsIconic/visibility state is not useful.
        // Release stale websocket protection when native foreground is
        // definitively not osu! so drag/interaction can resume after alt-tab.
        if (_osuFocused && IsForegroundDefinitelyNotOsu())
        {
            _osuFocused = false;
        }

        if (IsOsuInteractionBlocked())
        {
            ProtectForOsu();
            return;
        }

        // Once osu! is no longer the foreground user _window, the overlay is
        // an ordinary editable _window again. This covers every safe editing
        // state: osu! minimized, restored behind another application, or not
        // running at all. SetClickThrough also updates the WebView input
        // state through InteractionChanged.
        SetCursorHiddenForOsu(false);
        SetClickThrough(false);
    }

    private bool IsOsuInteractionBlocked() => _osuFocused || IsForegroundOsuProcess();

    private void SetOsuMinimized(bool minimized)
    {
        if (_osuMinimized == minimized)
        {
            return;
        }

        _osuMinimized = minimized;
        try
        {
            OsuWindowMinimizedChanged?.Invoke(minimized);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Reporting osu! window state", exception, userVisible: false);
        }
    }

    private void SetCursorHiddenForOsu(bool hidden)
    {
        if (_cursorHiddenForOsu == hidden || !IsSupported)
        {
            return;
        }

        _ = ShowCursor(!hidden);
        _cursorHiddenForOsu = hidden;
    }

    private static bool IsOsuProcessRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("osu!");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Checking osu! process", "The osu! process list could not be read.", exception);
            return false;
        }
    }

    private static bool IsForegroundOsuProcess()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || GetWindowThreadProcessId(foreground, out var processId) == 0 || processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return IsOsuProcessName(process.ProcessName);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Checking osu! foreground process", "The foreground process could not be inspected.", exception);
            return false;
        }
    }

    private static bool IsForegroundDefinitelyNotOsu()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        if (GetWindowThreadProcessId(foreground, out var processId) == 0 || processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return !IsOsuProcessName(process.ProcessName);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Checking osu! foreground process", "The foreground process could not be inspected.", exception);
            return false;
        }
    }

    private static bool IsOsuProcessName(string? processName) =>
        string.Equals(processName, "osu!", StringComparison.OrdinalIgnoreCase);

    private void SetInteractive(bool enabled)
    {
        if (_interactive == enabled)
        {
            return;
        }

        _interactive = enabled;
        InteractionChanged?.Invoke(enabled);
    }

    private IntPtr Handle => _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    private enum OsuWindowState
    {
        Unknown,
        None,
        Minimized,
        Restored
    }

    private static OsuWindowState GetOsuWindowState()
    {
        var processes = Process.GetProcessesByName("osu!");
        var minimized = false;
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero || !IsWindow(handle))
                    {
                        continue;
                    }

                    if (IsIconic(handle))
                    {
                        minimized = true;
                    }
                    else if (IsWindowVisible(handle))
                    {
                        return OsuWindowState.Restored;
                    }
                }
                catch (Exception exception)
                {
                    AppLogger.Warning("Inspecting osu! _window", "A candidate osu! _window could not be inspected.", exception);
                }
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Inspecting osu! windows", "The osu! _window state could not be determined.", exception);
            return OsuWindowState.Unknown;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
        return minimized ? OsuWindowState.Minimized : OsuWindowState.None;
    }

    private static void ReturnFocusToOsu()
    {
        var processes = Process.GetProcessesByName("osu!");
        try
        {
            var process = processes.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (process is not null)
            {
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Returning focus to osu!", "The osu! _window could not be focused.", exception);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _guardTimer.Stop();
            SetCursorHiddenForOsu(false);
            var handle = Handle;
            // Ensure a _window disabled for protected overlay input cannot remain
            // disabled after the controller is disposed.
            if (IsSupported)
            {
                EnableWindow(handle, true);
            }

            if (_registered && _hotkeyHandle != IntPtr.Zero)
            {
                UnregisterHotKey(_hotkeyHandle, ExitHotkeyId);
                UnregisterHotKey(_hotkeyHandle, InputHotkeyId);
            }
            Win32Properties.RemoveWndProcHookCallback(_window, _callback);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Disposing Windows overlay controller", exception, userVisible: false);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enable);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

}
