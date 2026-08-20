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

    private readonly Window window;
    private readonly Win32Properties.CustomWndProcHookCallback callback;
    private readonly DispatcherTimer guardTimer;
    private bool registered;
    private bool overlayMode;
    private bool clickThrough;
    private bool interactive;
    private bool osuFocused;
    private bool? osuProcessRunning;

    public WindowsOverlayController(Window window)
    {
        this.window = window;
        callback = WndProc;
        Win32Properties.AddWndProcHookCallback(window, callback);
        guardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        guardTimer.Tick += (_, _) =>
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
    public bool IsClickThrough => clickThrough;
    public bool IsOsuFocused => osuFocused;

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
        if (!IsSupported || registered)
            return IsSupported;
        var handle = Handle;
        if (handle == IntPtr.Zero)
            return false;
        var exit = RegisterHotKey(handle, ExitHotkeyId, ModControl | ModShift, VkF10);
        var input = RegisterHotKey(handle, InputHotkeyId, ModControl | ModShift, VkF9);
        registered = exit && input;
        return registered;
    }

    public void Enter()
    {
        overlayMode = true;
        // A new overlay session must not inherit a stale websocket focus
        // signal from a previous session. The native foreground-process
        // check below remains the safety net until the watcher reports the
        // current value.
        osuFocused = false;
        osuProcessRunning = null;
        SetClickThrough(true);
        guardTimer.Start();
        // Keep the existing overlay behavior for a live osu! window, but do
        // not restore/focus osu! when it is already minimized.
        if (IsSupported && GetOsuWindowState() == OsuWindowState.Restored)
            ReturnFocusToOsu();
        SynchronizeWithOsuWindow();
    }

    public void Leave()
    {
        guardTimer.Stop();
        osuFocused = false;
        osuProcessRunning = null;
        // Protected overlay mode disables the top-level window. Re-enable it
        // before changing mode so Avalonia/WebView can be used normally again.
        if (IsSupported)
            EnableWindow(Handle, true);
        overlayMode = false;
        SetClickThrough(false);
        SetInteractive(false);
        ApplyStyles();
    }

    public void ToggleInput()
    {
        if (!overlayMode)
            return;
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
        if (clickThrough && state == OsuWindowState.Restored)
        {
            ReturnFocusToOsu();
            return;
        }
        SetClickThrough(!clickThrough);
        if (clickThrough && state == OsuWindowState.Restored)
            ReturnFocusToOsu();
    }

    public void BeginDrag()
    {
        if (!overlayMode || !interactive || clickThrough || IsOsuInteractionBlocked() || Handle == IntPtr.Zero)
            return;
        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    public void BeginResize(string direction)
    {
        if (!overlayMode || !interactive || clickThrough || IsOsuInteractionBlocked() || !window.CanResize || Handle == IntPtr.Zero)
            return;

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
            return;

        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, (IntPtr)hitTest, IntPtr.Zero);
    }

    public void SetClickThrough(bool enabled)
    {
        // A hotkey or a stale UI callback must not unlock the overlay while
        // osu! is the active user window. The guard will reevaluate this when
        // focus changes or the foreground process changes.
        if (!enabled && overlayMode && IsOsuInteractionBlocked())
            enabled = true;
        if (clickThrough == enabled)
        {
            ApplyStyles();
            SetInteractive(overlayMode && !clickThrough && !IsOsuInteractionBlocked());
            return;
        }
        clickThrough = enabled;
        ApplyStyles();
        ClickThroughChanged?.Invoke(enabled);
        SetInteractive(overlayMode && !clickThrough && !IsOsuInteractionBlocked());
    }

    public void SetOsuFocused(bool focused)
    {
        osuFocused = focused;
        if (!overlayMode)
            return;

        if (focused)
        {
            // Apply protection synchronously: the 250 ms guard interval is
            // intentionally not part of the focus-signal safety path.
            ProtectForOsu();
            return;
        }

        // A focus=false message releases the websocket-side protection. The
        // native foreground-process check remains the final safety net while
        // the game is still the active user window.
        SynchronizeWithOsuWindow();
    }

    public void SetWindowVisible(bool visible)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("Native overlay visibility is only available on Windows.");

        var handle = Handle;
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("The overlay window handle is not available.");

        ShowWindow(handle, visible ? SwShowNoActivate : SwHide);
        if (IsWindowVisible(handle) != visible)
        {
            throw new InvalidOperationException(
                visible
                    ? "Windows did not show the overlay window."
                    : "Windows did not hide the overlay window.");
        }
    }

    private void ProtectForOsu()
    {
        if (!overlayMode)
            return;
        SetClickThrough(true);
        SetInteractive(false);
        ApplyStyles();
    }

    private void ApplyStyles()
    {
        if (!IsSupported || Handle == IntPtr.Zero)
            return;
        var styles = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        var windowStyles = GetWindowLongPtr(Handle, GwlStyle).ToInt64();
        if (overlayMode)
        {
            styles |= WsExToolWindow | WsExNoActivate;
            if (clickThrough)
                styles |= WsExTransparent;
            else
                styles &= ~WsExTransparent;
            // Avalonia may remove the resize frame while CanResize is false.
            // Keep the native frame available; custom hit testing below only
            // exposes it while the widget is interactive.
            windowStyles |= WsThickFrame;
        }
        else
        {
            styles &= ~(WsExToolWindow | WsExNoActivate | WsExTransparent);
        }
        SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(styles));
        // Do not route protected clicks through the overlay. A disabled
        // top-level window keeps both Avalonia and WebView child HWNDs from
        // receiving the click, while still leaving the overlay visible.
        // EnableWindow does not unregister RegisterHotKey bindings; WM_HOTKEY
        // remains dispatched to this window's hook while it is disabled.
        var protectedInput = overlayMode && (clickThrough || osuFocused || IsForegroundOsuProcess());
        EnableWindow(Handle, !protectedInput);
        if (overlayMode && windowStyles != GetWindowLongPtr(Handle, GwlStyle).ToInt64())
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
            // The protected top-level window is disabled in ApplyStyles, but keep
            // the native hit-test inside this window if Windows asks for one. In
            // particular, never return HTTRANSPARENT: that can route a click to
            // osu! or another window underneath the overlay.
            if (message == WmNcHitTest && overlayMode && clickThrough)
            {
                handled = true;
                return (IntPtr)HtClient;
            }
            if (message == WmNcHitTest && interactive && !clickThrough && window.CanResize)
            {
                var hitTest = GetResizeHitTest(lParam);
                if (hitTest != HtClient)
                {
                    handled = true;
                    return (IntPtr)hitTest;
                }
            }
            if (message == WmMouseActivate && overlayMode && clickThrough)
            {
                handled = true;
                return (IntPtr)MaNoActivateAndEat;
            }
            if (message != WmHotkey)
                return IntPtr.Zero;
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
            // This callback is entered from an unmanaged Win32 window
            // procedure. Letting an exception escape it terminates the CLR with
            // 0xC000041D instead of reaching Avalonia's normal error handling.
            handled = true;
            AppLogger.Error($"Processing overlay window message 0x{message:X}", exception);
            return IntPtr.Zero;
        }
    }

    private void SynchronizeWithOsuWindow()
    {
        if (!overlayMode || !IsSupported)
            return;

        var processRunning = IsOsuProcessRunning();
        if (osuProcessRunning != processRunning)
        {
            osuProcessRunning = processRunning;
            try
            {
                OsuProcessChanged?.Invoke(processRunning);
            }
            catch (Exception exception) { AppLogger.Error("Reporting osu! process state", exception, userVisible: false); }
        }

        // The overlay belongs to the game session. Once osu! exits there is no
        // safe foreground window to protect against, so return control to the
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

        // Once osu! is no longer the foreground user window, the overlay is
        // an ordinary editable window again. This covers every safe editing
        // state: osu! minimized, restored behind another application, or not
        // running at all. SetClickThrough also updates the native resize
        // frame and Avalonia's CanResize state through InteractionChanged.
        SetClickThrough(false);
    }

    private bool IsOsuInteractionBlocked() => osuFocused || IsForegroundOsuProcess();

    private static bool IsOsuProcessRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("osu!");
            try
            {
                return processes.Length > 0;
            }
            finally { foreach (var process in processes) process.Dispose(); }
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
            return false;

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
        if (interactive == enabled)
            return;
        interactive = enabled;
        InteractionChanged?.Invoke(enabled);
    }

    private int GetResizeHitTest(IntPtr lParam)
    {
        if (Handle == IntPtr.Zero || !GetWindowRect(Handle, out var rectangle))
            return HtClient;
        if (!TryGetScreenPoint(lParam, out var point))
            return HtClient;

        var border = Math.Max(6, GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder));
        var left = point.X >= rectangle.Left && point.X < rectangle.Left + border;
        var right = point.X < rectangle.Right && point.X >= rectangle.Right - border;
        var top = point.Y >= rectangle.Top && point.Y < rectangle.Top + border;
        var bottom = point.Y < rectangle.Bottom && point.Y >= rectangle.Bottom - border;

        if (left && top)
            return HtTopLeft;
        if (right && top)
            return HtTopRight;
        if (left && bottom)
            return HtBottomLeft;
        if (right && bottom)
            return HtBottomRight;
        if (left)
            return HtLeft;
        if (right)
            return HtRight;
        if (top)
            return HtTop;
        if (bottom)
            return HtBottom;
        return HtClient;
    }

    private static bool TryGetScreenPoint(IntPtr lParam, out POINT point)
    {
        if (GetCursorPos(out point))
            return true;
        var value = lParam.ToInt64();
        point = new POINT(unchecked((short)(value & 0xFFFF)), unchecked((short)((value >> 16) & 0xFFFF)));
        return true;
    }

    private IntPtr Handle => window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

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
                        continue;
                    if (IsIconic(handle))
                        minimized = true;
                    else
                        return OsuWindowState.Restored;
                }
                catch (Exception exception)
                {
                    AppLogger.Warning("Inspecting osu! window", "A candidate osu! window could not be inspected.", exception);
                }
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Inspecting osu! windows", "The osu! window state could not be determined.", exception);
            return OsuWindowState.Unknown;
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return minimized ? OsuWindowState.Minimized : OsuWindowState.None;
    }

    private static void ReturnFocusToOsu()
    {
        var processes = Process.GetProcessesByName("osu!");
        try
        {
            var process = processes.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (process is not null)
                SetForegroundWindow(process.MainWindowHandle);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Returning focus to osu!", "The osu! window could not be focused.", exception);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public void Dispose()
    {
        try
        {
            guardTimer.Stop();
            var handle = Handle;
            // Ensure a window disabled for protected overlay input cannot remain
            // disabled after the controller is disposed.
            if (IsSupported)
                EnableWindow(handle, true);
            if (registered && handle != IntPtr.Zero)
            {
                UnregisterHotKey(handle, ExitHotkeyId);
                UnregisterHotKey(handle, InputHotkeyId);
            }
            Win32Properties.RemoveWndProcHookCallback(window, callback);
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
