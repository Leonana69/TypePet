using System;

namespace TypePet.Platform.Abstractions;

/// <summary>
/// Native effects on the transparent overlay window itself: making it click-through, hiding/showing it
/// without stealing focus, and keeping it pinned at the top of the z-order. The implementation owns any
/// "re-assert topmost" timer (Windows fights borderless-fullscreen apps that raise above it; macOS
/// re-applies its window level because Avalonia can clobber it). PetWindow never touches the OS handle.
/// </summary>
public interface IOverlayEffects : IDisposable
{
    /// <summary>One-time setup once the native window handle exists (PetWindow.OnOpened).
    /// Windows: WS_EX_TRANSPARENT|LAYERED|NOACTIVATE and start the topmost re-assert timer.
    /// macOS: set the window level above the menu bar, ignoresMouseEvents=true, and a collection
    /// behavior so the overlay spans every Space and floats over fullscreen apps.</summary>
    void ConfigureOverlay(nint nativeHandle);

    /// <summary>Toggle click-through. Windows adds/removes WS_EX_TRANSPARENT; macOS sets
    /// NSWindow.ignoresMouseEvents. The overlay is click-through by default on both.</summary>
    void SetClickThrough(bool clickThrough);

    /// <summary>Hide the overlay (a fullscreen app took the foreground). Windows: SW_HIDE.
    /// macOS: orderOut:.</summary>
    void Hide();

    /// <summary>Re-show the overlay WITHOUT activating it, preserving the never-steal-focus promise,
    /// and re-assert its top-of-z-order position. Windows: SW_SHOWNOACTIVATE + RaiseToTop.
    /// macOS: orderFrontRegardless + re-apply level/collection behavior.</summary>
    void ShowNoActivate();

    /// <summary>While true the implementation stops re-asserting topmost z-order, so a focusable window
    /// (the say-input bar) can sit above the overlay. Windows: pauses the RaiseToTop timer.
    /// macOS: lowers the overlay's window level so the bar isn't buried (NOT a no-op).</summary>
    bool SuppressTopmost { get; set; }
}
