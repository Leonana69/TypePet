using Avalonia.Controls;
using MaplePet.Platform.Abstractions;

namespace MaplePet.Platform.Mac;

/// <summary>The macOS bundle of window-bound platform services, created once the overlay window exists.
/// The overlay's NSWindow handle is reached lazily by the overlay/input impls in Step 8.</summary>
public sealed class MacPlatformServices : IPlatformServices
{
    public IWindowTracker  WindowTracker  { get; } = new MacWindowTracker();
    public IOverlayEffects OverlayEffects { get; } = new MacOverlayEffects();
    public IPetInput       Input          { get; }
    public IGlobalHotkey   Hotkey         { get; } = new MacGlobalHotkey();
    // (window tracker / overlay / hotkey above need no window; input does — built in the ctor)
    public ITrayGlyphs     TrayGlyphs     => PlatformServices.TrayGlyphs;

    public MacPlatformServices(Window overlay)
    {
        Input = new MacPetInput(overlay); // needs the overlay window for pointer events + the NSWindow
    }
}
