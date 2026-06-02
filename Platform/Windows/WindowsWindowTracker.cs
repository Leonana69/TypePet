using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
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
