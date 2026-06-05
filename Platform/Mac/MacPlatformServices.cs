using System;
using Avalonia.Controls;
using MaplePet.Platform.Abstractions;

namespace MaplePet.Platform.Mac;

/// <summary>The macOS bundle of window-bound platform services, created once the overlay window exists.
/// The overlay's NSWindow handle is reached lazily by the overlay/input impls in Step 8.</summary>
public sealed class MacPlatformServices : IPlatformServices
{
    private readonly MacWindowTracker _tracker = new();

    public IWindowTracker  WindowTracker  => _tracker;
    public IOverlayEffects OverlayEffects { get; } = new MacOverlayEffects();
    public IPetInput       Input          { get; }
    public IGlobalHotkey   Hotkey         { get; } = new MacGlobalHotkey();
    // (window tracker / overlay / hotkey above need no window; input does — built in the ctor)
    public ITrayGlyphs     TrayGlyphs     => PlatformServices.TrayGlyphs;

    public MacPlatformServices(Window overlay)
    {
        Input = new MacPetInput(overlay); // needs the overlay window for pointer events + the NSWindow

        // Exclude our own full-screen overlay from the captured world (the Windows bundle does the same
        // with ExcludeHwnd): match it by NSWindow.windowNumber, which CGWindowList reports as
        // kCGWindowNumber. The overlay's native handle already exists here (resolved in
        // PetWindow.OnOpened), so the number is live before the first world poll. Other MaplePet windows
        // (the config/character window, the say bar) are left in the world as walkable platforms.
        _tracker.ExcludeWindowNumber = MacNative.NSWindowNumber(overlay.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }
}
