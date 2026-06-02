using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MaplePet.Platform.Windows;

/// <summary>Win32 helpers for the overlay window itself (click-through toggle, cursor/mouse polling).</summary>
public static class WindowsInterop
{
    private const int VK_LBUTTON = 0x01;

    /// <summary>Make the overlay click-through (input passes to the apps behind it).</summary>
    public static void MakeClickThrough(nint hwnd) => SetClickThrough(hwnd, true);

    /// <summary>
    /// Toggle the overlay between click-through and interactive. WS_EX_LAYERED and
    /// WS_EX_NOACTIVATE are kept in both states (the overlay never steals focus); only
    /// WS_EX_TRANSPARENT is added (click-through) or removed (interactive, so a click on the
    /// pet is caught by the overlay instead of the window behind it).
    /// </summary>
    public static void SetClickThrough(nint hwnd, bool clickThrough)
    {
        var h = (HWND)hwnd;
        long ex = (long)PInvoke.GetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex |= (long)WINDOW_EX_STYLE.WS_EX_LAYERED | (long)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        if (clickThrough) ex |= (long)WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        else ex &= ~(long)WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        PInvoke.SetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)ex);
    }

    /// <summary>
    /// Ask the foreground window (and its children) to repaint. Flipping the overlay's click-through
    /// extended style on top of hardware-accelerated video can make the app below stop presenting its
    /// video plane — it stays black until the app repaints (which is why clicking it brings it back).
    /// Invalidating it when the overlay returns to click-through mimics that click so the video
    /// recovers on its own. Best-effort: a no-op if there is no foreground window.
    /// </summary>
    public static void RepaintForegroundWindow()
    {
        HWND fg = PInvoke.GetForegroundWindow();
        if (fg == HWND.Null) return;
        PInvoke.RedrawWindow(fg, (RECT?)null, (SafeHandle?)null,
            REDRAW_WINDOW_FLAGS.RDW_INVALIDATE | REDRAW_WINDOW_FLAGS.RDW_ERASE | REDRAW_WINDOW_FLAGS.RDW_ALLCHILDREN);
    }

    /// <summary>Current cursor position in physical screen pixels.</summary>
    public static bool TryGetCursorPos(out int x, out int y)
    {
        if (PInvoke.GetCursorPos(out var p)) { x = p.X; y = p.Y; return true; }
        x = 0; y = 0; return false;
    }

    /// <summary>True while the left mouse button is physically down (polled globally).</summary>
    public static bool IsLeftButtonDown() => (PInvoke.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
}
