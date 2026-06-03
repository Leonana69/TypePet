using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MaplePet.Platform.Windows;

/// <summary>Win32 helpers for the overlay window itself (click-through toggle, cursor/mouse polling).</summary>
public static class WindowsInterop
{
    private const int VK_LBUTTON = 0x01;

    // The special "insert above all non-topmost, at the top of the topmost band" HWND value
    // (#define HWND_TOPMOST ((HWND)-1)). Constructed the same way the rest of this file casts
    // an nint handle to HWND.
    private static readonly HWND HWND_TOPMOST = (HWND)(nint)(-1);

    /// <summary>Make the overlay click-through (input passes to the apps behind it).</summary>
    public static void MakeClickThrough(nint hwnd) => SetClickThrough(hwnd, true);

    /// <summary>
    /// Re-assert the overlay at the top of the topmost z-order WITHOUT activating it
    /// (SWP_NOACTIVATE keeps the WS_EX_NOACTIVATE promise — the overlay never steals focus).
    /// Avalonia sets WS_EX_TOPMOST once, but activating another topmost window (e.g. a
    /// borderless-fullscreen game) raises it above us within the topmost band; since this overlay
    /// never takes focus, it can't recover on its own. Calling this on a timer keeps the pet on
    /// top. (It's a harmless no-op over a true exclusive-fullscreen app, where DWM isn't
    /// compositing the desktop to that display at all — nothing short of render injection helps.)
    /// </summary>
    public static void RaiseToTop(nint hwnd)
    {
        if (hwnd == 0) return;
        PInvoke.SetWindowPos((HWND)hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

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

    /// <summary>Current cursor position in physical screen pixels.</summary>
    public static bool TryGetCursorPos(out int x, out int y)
    {
        if (PInvoke.GetCursorPos(out var p)) { x = p.X; y = p.Y; return true; }
        x = 0; y = 0; return false;
    }

    /// <summary>True while the left mouse button is physically down (polled globally).</summary>
    public static bool IsLeftButtonDown() => (PInvoke.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
}
