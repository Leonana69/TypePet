using Avalonia.Controls;
using TypePet.Platform.Abstractions;

namespace TypePet.Platform.Windows;

/// <summary>The Windows bundle of window-bound platform services, created once the overlay window
/// exists. Captures the overlay's HWND so the window tracker excludes it from the captured world.</summary>
public sealed class WindowsPlatformServices : IPlatformServices
{
    private readonly WindowsWindowTracker _tracker = new();

    public IWindowTracker  WindowTracker  => _tracker;
    public IOverlayEffects OverlayEffects { get; } = new WindowsOverlayEffects();
    public IPetInput       Input          { get; } = new WindowsPetInput();
    public IGlobalHotkey   Hotkey         { get; } = new HotkeyListener();
    public ITrayGlyphs     TrayGlyphs     => PlatformServices.TrayGlyphs;

    public WindowsPlatformServices(Window overlay)
    {
        // Exclude our own full-screen overlay from the captured world (it spans the whole screen and
        // would otherwise read as a giant platform). Other TypePet windows (say bar, config) are
        // ordinary small windows and stay walkable, matching the original behavior.
        _tracker.ExcludeHwnd = overlay.TryGetPlatformHandle()?.Handle ?? 0;
    }
}
