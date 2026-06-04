using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using MaplePet.Engine;
using MaplePet.Platform;
using ERect = MaplePet.Engine.Rect;

namespace MaplePet.Platform.Windows;

/// <summary>
/// The only class that touches Win32. Enumerates visible top-level windows, filters out
/// minimized/cloaked/tool windows, measures their TRUE visible bounds via DWM, and reads
/// the taskbar position. Everything is returned in physical pixels.
/// </summary>
public sealed class WindowsWindowTracker : IWindowTracker
{
    private const int MinWindowSize = 40;

    /// <summary>The overlay's own HWND, excluded from the captured world.</summary>
    public nint ExcludeHwnd { get; set; }

    public WorldGeometry Capture()
    {
        var handles = new List<HWND>(256);
        PInvoke.EnumWindows((h, _) => { handles.Add(h); return true; }, default);

        var windows = new List<ERect>();
        foreach (var h in handles)
        {
            if (h == (HWND)ExcludeHwnd) continue;
            if (!PInvoke.IsWindowVisible(h)) continue;
            if (PInvoke.IsIconic(h)) continue;             // minimized
            if (IsCloaked(h)) continue;                    // hidden UWP / other virtual desktop
            if (PInvoke.GetWindowTextLength(h) == 0) continue;
            if (IsToolWindow(h)) continue;

            var rect = GetVisibleRect(h);
            if (rect.Width < MinWindowSize || rect.Height < MinWindowSize) continue;
            windows.Add(rect);
        }

        var (taskbar, edge) = GetTaskbar();
        return new WorldGeometry(windows, taskbar, edge);
    }

    /// <summary>
    /// True when the FOREGROUND window is borderless / exclusive fullscreen — it covers its whole
    /// monitor, taskbar included. Three guards keep a normal desktop from tripping it:
    /// <list type="bullet">
    /// <item>The shell surfaces (desktop wallpaper host, taskbar) are excluded — they span the screen
    /// too, so clicking the empty desktop would otherwise read as "fullscreen".</item>
    /// <item>A normal app that's merely <b>maximized</b> still carries its title-bar / resize-frame
    /// style; it's excluded even when an auto-hide taskbar lets it cover the whole monitor. A
    /// borderless game drops that chrome, so it isn't excluded here.</item>
    /// <item>Finally the window's true visible bounds must cover the full monitor rect (not just the
    /// work area) — the test that actually separates fullscreen from a taskbar-respecting maximize.</item>
    /// </list>
    /// Only the foreground window is considered, so alt-tabbing out of a game (its window stays
    /// fullscreen-sized but loses focus) brings the pet back.
    /// </summary>
    public bool IsForegroundFullscreen()
    {
        var fg = PInvoke.GetForegroundWindow();
        if (fg == default || fg == (HWND)ExcludeHwnd) return false;
        return IsFullscreenWindow(fg);
    }

    /// <summary>True when the given window is borderless / exclusive fullscreen — its true visible bounds
    /// cover its whole monitor (taskbar included), rather than merely being maximized. The shell surfaces
    /// and framed-maximized windows are excluded (see the summary on <see cref="IsForegroundFullscreen"/>).</summary>
    private bool IsFullscreenWindow(HWND h)
    {
        if (IsShellWindow(h)) return false;

        // A framed window that's just maximized (e.g. over an auto-hide taskbar) is not fullscreen.
        if (PInvoke.IsZoomed(h) && HasCaption(h)) return false;

        var monitor = PInvoke.MonitorFromWindow(h, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (monitor == default) return false;
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref mi)) return false;
        var m = mi.rcMonitor;

        var w = GetVisibleRect(h);
        const double tol = 2; // borderless windows are sometimes a pixel shy of the exact monitor rect
        return w.Left <= m.left + tol && w.Top <= m.top + tol
            && w.Right >= m.right - tol && w.Bottom >= m.bottom - tol;
    }

    private static bool HasCaption(HWND h)
    {
        long style = (long)PInvoke.GetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        return (style & (long)WINDOW_STYLE.WS_CAPTION) == (long)WINDOW_STYLE.WS_CAPTION;
    }

    /// <summary>True for the desktop wallpaper hosts and taskbar windows, which also span the screen
    /// but must never count as a fullscreen app.</summary>
    private static unsafe bool IsShellWindow(HWND h)
    {
        char* buf = stackalloc char[64];
        int n = PInvoke.GetClassName(h, new PWSTR(buf), 64);
        if (n <= 0) return false;
        string cls = new(buf, 0, n);
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static bool IsToolWindow(HWND h)
    {
        long ex = (long)PInvoke.GetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        return (ex & (long)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0;
    }

    private static unsafe bool IsCloaked(HWND h)
    {
        int cloaked = 0;
        var hr = PInvoke.DwmGetWindowAttribute(
            h, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        return hr.Succeeded && cloaked != 0;
    }

    private static unsafe ERect GetVisibleRect(HWND h)
    {
        RECT r = default;
        var hr = PInvoke.DwmGetWindowAttribute(
            h, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT));
        if (hr.Failed)
            PInvoke.GetWindowRect(h, out r); // fallback (includes drop-shadow border)
        return new ERect(r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    private static (ERect, TaskbarEdge) GetTaskbar()
    {
        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        var result = PInvoke.SHAppBarMessage(PInvoke.ABM_GETTASKBARPOS, ref abd);
        if (result != UIntPtr.Zero)
        {
            var r = abd.rc;
            var edge = abd.uEdge switch
            {
                0u => TaskbarEdge.Left,
                1u => TaskbarEdge.Top,
                2u => TaskbarEdge.Right,
                _ => TaskbarEdge.Bottom,
            };
            return (new ERect(r.left, r.top, r.right - r.left, r.bottom - r.top), edge);
        }

        // Fallback: locate the primary taskbar window directly.
        var tray = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (tray != default && PInvoke.GetWindowRect(tray, out var tr))
            return (new ERect(tr.left, tr.top, tr.right - tr.left, tr.bottom - tr.top), TaskbarEdge.Bottom);

        return (new ERect(0, 0, 0, 0), TaskbarEdge.Bottom);
    }
}
