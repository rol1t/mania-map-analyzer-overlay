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
    private const int GwlStyle = -16;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsThickFrame = 0x00040000L;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;
    private const int HtClient = 0x0001;
    private const int HtLeft = 0x000A;
    private const int HtRight = 0x000B;
    private const int HtTop = 0x000C;
    private const int HtTopLeft = 0x000D;
    private const int HtTopRight = 0x000E;
    private const int HtBottom = 0x000F;
    private const int HtBottomLeft = 0x0010;
    private const int HtBottomRight = 0x0011;
    private const int MaNoActivateAndEat = 0x0004;
    private const int SmCxSizeFrame = 32;
    private const int SmCxPaddedBorder = 92;
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
    private bool? _osuProcessRunning;

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
        };
    }

    public event EventHandler? ExitRequested;
    public event Action<bool>? ClickThroughChanged;
    public event Action<bool>? InteractionChanged;
    public event Action<bool>? OsuProcessChanged;
    public bool IsSupported => OperatingSystem.IsWindows();
    public bool IsClickThrough => _clickThrough;
    public bool IsOsuFocused => _osuFocused;

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
        return _registered;
    }

    public void Enter()
    {
        _overlayMode = true;
        // A new overlay session must not inherit a stale websocket focus
        // signal from a previous session. The native foreground-process
        // check below remains the safety net until the watcher reports the
        // current value.
        _osuFocused = false;
        _osuProcessRunning = null;
        SetClickThrough(true);
        _guardTimer.Start();
        // Keep the existing overlay behavior for a live osu! _window, but do
        // not restore/focus osu! when it is already minimized.
        if (IsSupported && GetOsuWindowState() == OsuWindowState.Restored)
        {
            ReturnFocusToOsu();
        }

        SynchronizeWithOsuWindow();
    }

    public void Leave()
    {
        _guardTimer.Stop();
        _osuFocused = false;
        _osuProcessRunning = null;
        // Protected overlay mode disables the top-level _window. Re-enable it
        // before changing mode so Avalonia/WebView can be used normally again.
        if (IsSupported)
        {
            EnableWindow(Handle, true);
        }

        _overlayMode = false;
        SetClickThrough(false);
        SetInteractive(false);
        ApplyStyles();
    }

    public void ToggleInput()
    {
        if (!_overlayMode)
        {
            return;
        }

        if (IsOsuInteractionBlocked())
        {
            ProtectForOsu();
            return;
        }
        var state = IsSupported ? GetOsuWindowState() : OsuWindowState.Unknown;
        if (state == OsuWindowState.Minimized)
        {
            // Minimized osu! is the automatic editing mode. Keep the hotkey
            // useful without allowing it to make the widget click-through
            // until the next guard tick.
            SetClickThrough(false);
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
        SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    public void BeginResize(string direction)
    {
        if (!_overlayMode || !_interactive || _clickThrough || IsOsuInteractionBlocked() || !_window.CanResize || Handle == IntPtr.Zero)
        {
            return;
        }

        var hitTest = direction.Trim().ToLowerInvariant() switch
        {
            "n" => HtTop,
            "s" => HtBottom,
            "e" => HtRight,
            "w" => HtLeft,
            "ne" => HtTopRight,
            "nw" => HtTopLeft,
            "se" => HtBottomRight,
            "sw" => HtBottomLeft,
            _ => HtClient
        };
        if (hitTest == HtClient)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, (IntPtr)hitTest, IntPtr.Zero);
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
        var windowStyles = GetWindowLongPtr(Handle, GwlStyle).ToInt64();
        if (_overlayMode)
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
            // Avalonia may remove the resize frame while CanResize is false.
            // Keep the native frame available; custom hit testing below only
            // exposes it while the widget is _interactive.
            windowStyles |= WsThickFrame;
        }
        else
        {
            styles &= ~(WsExToolWindow | WsExNoActivate | WsExTransparent);
        }
        SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(styles));
        // Do not route protected clicks through the overlay. A disabled
        // top-level _window keeps both Avalonia and WebView child HWNDs from
        // receiving the click, while still leaving the overlay visible.
        // EnableWindow does not unregister RegisterHotKey bindings; WM_HOTKEY
        // remains dispatched to this _window's hook while it is disabled.
        var protectedInput = _overlayMode && (_clickThrough || _osuFocused || IsForegroundOsuProcess());
        EnableWindow(Handle, !protectedInput);
        if (_overlayMode && windowStyles != GetWindowLongPtr(Handle, GwlStyle).ToInt64())
        {
            SetWindowLongPtr(Handle, GwlStyle, new IntPtr(windowStyles));
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
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
            if (message == WmNcHitTest && _overlayMode && _clickThrough)
            {
                handled = true;
                return (IntPtr)HtClient;
            }
            if (message == WmNcHitTest && _interactive && !_clickThrough && _window.CanResize)
            {
                var hitTest = GetResizeHitTest(lParam);
                if (hitTest != HtClient)
                {
                    handled = true;
                    return (IntPtr)hitTest;
                }
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
            SetClickThrough(false);
            return;
        }

        // The websocket focus signal and the native foreground-process check
        // are independent protections. Either one is sufficient to keep the
        // top-level HWND disabled, even when osu! is fullscreen/borderless
        // and its IsIconic/visibility state is not useful.
        if (IsOsuInteractionBlocked())
        {
            ProtectForOsu();
            return;
        }

        // Once osu! is no longer the foreground user _window, the overlay is
        // an ordinary editable _window again. This covers every safe editing
        // state: osu! minimized, restored behind another application, or not
        // running at all. SetClickThrough also updates the native resize
        // frame and Avalonia's CanResize state through InteractionChanged.
        SetClickThrough(false);
    }

    private bool IsOsuInteractionBlocked() => _osuFocused || IsForegroundOsuProcess();

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

    private int GetResizeHitTest(IntPtr lParam)
    {
        if (Handle == IntPtr.Zero || !GetWindowRect(Handle, out var rectangle))
        {
            return HtClient;
        }

        if (!TryGetScreenPoint(lParam, out var point))
        {
            return HtClient;
        }

        var border = Math.Max(6, GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder));
        var left = point.X >= rectangle.Left && point.X < rectangle.Left + border;
        var right = point.X < rectangle.Right && point.X >= rectangle.Right - border;
        var top = point.Y >= rectangle.Top && point.Y < rectangle.Top + border;
        var bottom = point.Y < rectangle.Bottom && point.Y >= rectangle.Bottom - border;

        if (left && top)
        {
            return HtTopLeft;
        }

        if (right && top)
        {
            return HtTopRight;
        }

        if (left && bottom)
        {
            return HtBottomLeft;
        }

        if (right && bottom)
        {
            return HtBottomRight;
        }

        if (left)
        {
            return HtLeft;
        }

        if (right)
        {
            return HtRight;
        }

        if (top)
        {
            return HtTop;
        }

        if (bottom)
        {
            return HtBottom;
        }

        return HtClient;
    }

    private static bool TryGetScreenPoint(IntPtr lParam, out POINT point)
    {
        if (GetCursorPos(out point))
        {
            return true;
        }

        var value = lParam.ToInt64();
        point = new POINT(unchecked((short)(value & 0xFFFF)), unchecked((short)((value >> 16) & 0xFFFF)));
        return true;
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
                    if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle))
                    {
                        continue;
                    }

                    if (IsIconic(handle))
                    {
                        minimized = true;
                    }
                    else
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
            var handle = Handle;
            // Ensure a _window disabled for protected overlay input cannot remain
            // disabled after the controller is disposed.
            if (IsSupported)
            {
                EnableWindow(handle, true);
            }

            if (_registered && handle != IntPtr.Zero)
            {
                UnregisterHotKey(handle, ExitHotkeyId);
                UnregisterHotKey(handle, InputHotkeyId);
            }
            Win32Properties.RemoveWndProcHookCallback(_window, _callback);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Disposing Windows overlay controller", exception, userVisible: false);
        }
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enable);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rectangle);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct POINT
    {
        public readonly int X;
        public readonly int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
