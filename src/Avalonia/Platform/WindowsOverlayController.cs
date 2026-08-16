using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ManiaMapAnalyzerOverlay.Avalonia.Platform;

public sealed class WindowsOverlayController : IDisposable
{
    private const int ExitHotkeyId = 0x4D41;
    private const int InputHotkeyId = 0x4D42;
    private const uint WmHotkey = 0x0312;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    private readonly Window window;
    private readonly Win32Properties.CustomWndProcHookCallback callback;
    private readonly DispatcherTimer guardTimer;
    private bool registered;
    private bool overlayMode;
    private bool clickThrough;

    public WindowsOverlayController(Window window)
    {
        this.window = window;
        callback = WndProc;
        Win32Properties.AddWndProcHookCallback(window, callback);
        guardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        guardTimer.Tick += (_, _) =>
        {
            if (overlayMode && !clickThrough && IsOsuWindowRestored())
            {
                SetClickThrough(true);
                ReturnFocusToOsu();
            }
        };
    }

    public event EventHandler? ExitRequested;
    public event Action<bool>? ClickThroughChanged;
    public bool IsSupported => OperatingSystem.IsWindows();
    public bool IsClickThrough => clickThrough;

    public bool RegisterHotkeys()
    {
        if (!IsSupported || registered) return IsSupported;
        var handle = Handle;
        if (handle == IntPtr.Zero) return false;
        var exit = RegisterHotKey(handle, ExitHotkeyId, ModControl | ModShift, VkF10);
        var input = RegisterHotKey(handle, InputHotkeyId, ModControl | ModShift, VkF9);
        registered = exit && input;
        return registered;
    }

    public void Enter()
    {
        overlayMode = true;
        SetClickThrough(true);
        ReturnFocusToOsu();
        guardTimer.Start();
    }

    public void Leave()
    {
        guardTimer.Stop();
        SetClickThrough(false);
        overlayMode = false;
        ApplyStyles();
    }

    public void ToggleInput()
    {
        if (!overlayMode) return;
        if (clickThrough && IsOsuWindowRestored())
        {
            ReturnFocusToOsu();
            return;
        }
        SetClickThrough(!clickThrough);
        if (clickThrough) ReturnFocusToOsu();
    }

    public void BeginDrag()
    {
        if (!overlayMode || clickThrough || Handle == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    public void SetClickThrough(bool enabled)
    {
        clickThrough = enabled;
        ApplyStyles();
        ClickThroughChanged?.Invoke(enabled);
    }

    private void ApplyStyles()
    {
        if (!IsSupported || Handle == IntPtr.Zero) return;
        var styles = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        if (overlayMode)
        {
            styles |= WsExToolWindow | WsExNoActivate;
            if (clickThrough) styles |= WsExTransparent;
            else styles &= ~WsExTransparent;
        }
        else
        {
            styles &= ~(WsExToolWindow | WsExNoActivate | WsExTransparent);
        }
        SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(styles));
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey) return IntPtr.Zero;
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

    private IntPtr Handle => window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    private static bool IsOsuWindowRestored()
    {
        var processes = Process.GetProcessesByName("osu!");
        try
        {
            return processes.Any(p => p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle) && !IsIconic(p.MainWindowHandle));
        }
        catch { return false; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static void ReturnFocusToOsu()
    {
        var processes = Process.GetProcessesByName("osu!");
        try
        {
            var process = processes.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (process is not null) SetForegroundWindow(process.MainWindowHandle);
        }
        catch { }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public void Dispose()
    {
        guardTimer.Stop();
        if (registered && Handle != IntPtr.Zero)
        {
            UnregisterHotKey(Handle, ExitHotkeyId);
            UnregisterHotKey(Handle, InputHotkeyId);
        }
        Win32Properties.RemoveWndProcHookCallback(window, callback);
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
