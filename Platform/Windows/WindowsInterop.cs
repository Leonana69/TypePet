using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MaplePet.Platform.Windows;

/// <summary>Win32 helpers for the overlay window itself (click-through).</summary>
public static class WindowsInterop
{
    /// <summary>
    /// Make a window click-through by adding WS_EX_TRANSPARENT | WS_EX_LAYERED to its
    /// extended style. Mouse input then passes to whatever is behind the overlay.
    /// (Avalonia alone won't do this — see IMPLEMENTATION_PLAN.md section 4.)
    /// </summary>
    public static void MakeClickThrough(nint hwnd)
    {
        var h = (HWND)hwnd;
        long current = (long)PInvoke.GetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        long updated = current
            | (long)WINDOW_EX_STYLE.WS_EX_TRANSPARENT
            | (long)WINDOW_EX_STYLE.WS_EX_LAYERED;
        PInvoke.SetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)updated);
    }
}
