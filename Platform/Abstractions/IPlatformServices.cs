namespace TypePet.Platform.Abstractions;

/// <summary>
/// The bundle of window-bound platform services for the running OS, resolved by the
/// <c>PlatformServices</c> factory once the overlay window exists. Process-lifetime services that exist
/// before any window (single-instance, startup-at-login, app paths) are reached directly on the factory.
/// </summary>
public interface IPlatformServices
{
    IWindowTracker  WindowTracker  { get; }
    IOverlayEffects OverlayEffects { get; }
    IPetInput       Input          { get; }
    IGlobalHotkey   Hotkey         { get; }
    ITrayGlyphs     TrayGlyphs     { get; }
}
