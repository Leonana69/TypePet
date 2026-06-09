using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TypePet.Platform.Windows;

/// <summary>Win32 helpers for the overlay window itself (click-through toggle, cursor/mouse polling).</summary>
public static class WindowsInterop
{
    private const int VK_LBUTTON = 0x01;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4; // left Alt
    private const int VK_RMENU = 0xA5; // right Alt (AltGr on layouts that have it)

    // The special "insert above all non-topmost, at the top of the topmost band" HWND value
    // (#define HWND_TOPMOST ((HWND)-1)). Constructed the same way the rest of this file casts
    // an nint handle to HWND.
    private static readonly HWND HWND_TOPMOST = (HWND)(nint)(-1);

    /// <summary>Make the overlay click-through (input passes to the apps behind it).</summary>
    public static void MakeClickThrough(nint hwnd) => SetClickThrough(hwnd, true);

    /// <summary>Hide the overlay window (used while a fullscreen app is foreground). SW_HIDE also lets
    /// an exclusive-fullscreen game keep true fullscreen — a visible topmost overlay can force it into
    /// the slower composited path.</summary>
    public static void Hide(nint hwnd)
    {
        if (hwnd != 0) PInvoke.ShowWindow((HWND)hwnd, SHOW_WINDOW_CMD.SW_HIDE);
    }

    /// <summary>Re-show the overlay WITHOUT activating it, preserving the WS_EX_NOACTIVATE promise so
    /// it never steals focus from whatever the user is now using.</summary>
    public static void ShowNoActivate(nint hwnd)
    {
        if (hwnd != 0) PInvoke.ShowWindow((HWND)hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

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

    /// <summary>
    /// Force <paramref name="hwnd"/> to the foreground and give it keyboard focus, bypassing Windows'
    /// foreground lock. When a GLOBAL HOTKEY fires while another app is active we are a background
    /// process, and Windows then lets <c>SetForegroundWindow</c> only raise the window's taskbar button —
    /// not actually focus it — so the say/chat bar would appear but swallow no typing. The standard
    /// workaround is to briefly attach our input queue to the current foreground thread's, so the OS
    /// treats the focus change as coming from the active app and honours it.
    /// </summary>
    public static void ForceForeground(nint hwnd)
    {
        if (hwnd == 0) return;
        var target = (HWND)hwnd;

        HWND fg = PInvoke.GetForegroundWindow();
        uint thisThread = PInvoke.GetCurrentThreadId();
        uint fgThread = fg == HWND.Null ? 0 : PInvoke.GetWindowThreadProcessId(fg, out uint _);

        bool attached = fgThread != 0 && fgThread != thisThread
                        && PInvoke.AttachThreadInput(thisThread, fgThread, true);
        try
        {
            PInvoke.BringWindowToTop(target);
            PInvoke.SetForegroundWindow(target);
            PInvoke.SetFocus(target);
        }
        finally
        {
            if (attached) PInvoke.AttachThreadInput(thisThread, fgThread, false);
        }
    }

    /// <summary>Current cursor position in physical screen pixels.</summary>
    public static bool TryGetCursorPos(out int x, out int y)
    {
        if (PInvoke.GetCursorPos(out var p)) { x = p.X; y = p.Y; return true; }
        x = 0; y = 0; return false;
    }

    /// <summary>True while the left mouse button is physically down (polled globally).</summary>
    public static bool IsLeftButtonDown() => (PInvoke.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    // Physical modifier-key state, polled globally — used by the hotkey hook to confirm the chord's
    // modifiers. Same high-bit test as IsLeftButtonDown.
    public static bool IsCtrlDown() => Down(VK_CONTROL);
    public static bool IsAltDown() => Down(VK_MENU);
    public static bool IsShiftDown() => Down(VK_SHIFT);
    public static bool IsWinDown() => Down(VK_LWIN) || Down(VK_RWIN);
    private static bool Down(int vk) => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>True when the down modifiers look like AltGr (delivered as LeftCtrl + RightAlt on
    /// international layouts) rather than a deliberate Ctrl+Alt: RightAlt is down with no LeftAlt and
    /// no RightCtrl. A Ctrl+Alt hotkey checks this so it doesn't fire on — and eat — AltGr characters.</summary>
    public static bool LooksLikeAltGr() => Down(VK_RMENU) && !Down(VK_LMENU) && !Down(VK_RCONTROL);

    /// <summary>The system double-click interval in milliseconds (GetDoubleClickTime), used to
    /// synthesize a double-click from the global mouse poll. Falls back to 500ms.</summary>
    public static int DoubleClickTimeMs()
    {
        uint t = PInvoke.GetDoubleClickTime();
        return t > 0 ? (int)t : 500;
    }
}
